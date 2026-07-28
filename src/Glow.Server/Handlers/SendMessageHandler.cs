using Glow.Server.Instances;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// SendMessage: computes the receiver set (Others / All / Master / explicit
// peer ids / interest group) then broadcasts IncomingMessage using the
// sender-specified DeliveryMode + Channel end-to-end. Successful sends
// produce no ack. Cache directives applied last.
public static class SendMessageHandler
{
    public static void Handle(GlowServer server, Session session, Shared.Messages.SendMessage msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        if (msg.Channel >= server.Options.ChannelsCount)
        {
            server.SendError(session, msg.RequestId, ErrorCode.InvalidMessage,
                $"Channel {msg.Channel} out of range (max {server.Options.ChannelsCount - 1}).");
            return;
        }
        var instance = session.CurrentInstance!;
        var sender = session.CurrentPeer!;
        if (!AuthorizeStateMessage(instance, sender.PeerId, msg))
        {
            server.LogEvent($"[Instance] rejected unauthorized state code={msg.MessageCode} peer={sender.PeerId}");
            return;
        }

        List<Session> targets;
        switch (msg.Routing)
        {
            case Routing.Peers:
                targets = ResolvePeers(server, instance, msg.TargetPeers ?? Array.Empty<int>());
                break;
            case Routing.Group when msg.InterestGroup != 0:
                targets = ResolveGroup(server, instance, msg.InterestGroup);
                break;
            case Routing.Master:
                targets = ResolveMaster(server, instance);
                break;
            case Routing.All:
                targets = new List<Session>(server.InstanceSessions(instance));
                break;
            case Routing.Others:
            default:
                targets = new List<Session>();
                foreach (var s in server.InstanceSessions(instance))
                    if (s.ConnectionId != session.ConnectionId) targets.Add(s);
                break;
        }

        ApplyCache(instance, sender.PeerId, msg);

        var outgoing = new IncomingMessage(sender.PeerId, msg.MessageCode, msg.Delivery, msg.Channel, msg.Payload);
        server.Broadcast(targets, outgoing, msg.Delivery.ToTransport(), msg.Channel);
    }

    static bool AuthorizeStateMessage(Instance instance, int senderPeerId,
        Shared.Messages.SendMessage msg)
    {
        if (msg.MessageCode is not (20 or 21 or 25 or 26 or 27)) return true;
        try
        {
            var reader = new PayloadReader(msg.Payload);
            var networkId = reader.GetInt();
            if (networkId <= 0) return false;

            if (networkId >= 100_000)
                return networkId / 100_000 == senderPeerId;

            if (msg.MessageCode == 27)
            {
                var occupier = reader.GetInt();
                return occupier <= 0 || occupier == senderPeerId;
            }

            if (msg.MessageCode == 26)
            {
                _ = reader.GetByte();
                var holder = reader.GetInt();
                if (holder > 0 && holder != senderPeerId) return false;
            }

            return !instance.ObjectOwners.TryGetValue(networkId, out var owner)
                   || owner == senderPeerId;
        }
        catch
        {
            return false;
        }
    }

    static List<Session> ResolvePeers(GlowServer server, Instance instance, int[] ids)
    {
        var list = new List<Session>(ids.Length);
        foreach (var pid in ids)
        {
            if (instance.Peers.TryGetValue(pid, out var p) && p.IsActive &&
                p.ConnectionId is int cid &&
                server.Transport.TryGetSession(cid, out var s))
            {
                list.Add(s);
            }
        }
        return list;
    }

    static List<Session> ResolveGroup(GlowServer server, Instance instance, byte group)
    {
        var list = new List<Session>();
        foreach (var p in instance.ActivePeers)
        {
            if (p.SubscribedGroups.Contains(group) &&
                p.ConnectionId is int cid &&
                server.Transport.TryGetSession(cid, out var s))
            {
                list.Add(s);
            }
        }
        return list;
    }

    static List<Session> ResolveMaster(GlowServer server, Instance instance)
    {
        var list = new List<Session>(1);
        if (instance.MasterPeerId == 0) return list;
        if (instance.Peers.TryGetValue(instance.MasterPeerId, out var master) &&
            master.ConnectionId is int cid &&
            server.Transport.TryGetSession(cid, out var s))
        {
            list.Add(s);
        }
        return list;
    }

    static void ApplyCache(Instance instance, int senderPeerId, Shared.Messages.SendMessage msg)
    {
        if (msg.Cache == CachePolicy.None) return;
        var isTargeted = msg.Routing switch
        {
            Routing.Peers => true,
            Routing.Master => true,
            Routing.Group => msg.InterestGroup != 0,
            _ => false,
        };
        if (isTargeted &&
            msg.Cache is not (CachePolicy.RemoveByCode or CachePolicy.RemoveDeparted))
        {
            return;
        }

        switch (msg.Cache)
        {
            case CachePolicy.AddPerPeer:
                instance.Cache.Add(senderPeerId, msg.MessageCode, msg.Delivery, msg.Channel, msg.Payload);
                break;
            case CachePolicy.AddGlobal:
                instance.Cache.Add(0, msg.MessageCode, msg.Delivery, msg.Channel, msg.Payload);
                break;
            case CachePolicy.RemoveByCode:
                instance.Cache.RemoveByCode(msg.MessageCode, senderPeerId);
                break;
            case CachePolicy.RemoveDeparted:
                foreach (var pid in instance.Peers.Keys.ToArray())
                    if (!instance.Peers[pid].IsActive) instance.Cache.RemoveForPeer(pid);
                break;
            case CachePolicy.ReplaceLatest:
                // Per-(sender, code, CacheKey) single-entry bucket. Drop the
                // previous snapshot for this key, then append the new one so
                // late joiners see only the most recent state.
                instance.Cache.RemoveByCodeAndKey(msg.MessageCode, senderPeerId, msg.CacheKey);
                instance.Cache.Add(senderPeerId, msg.MessageCode, msg.Delivery, msg.Channel, msg.Payload, msg.CacheKey);
                break;
            case CachePolicy.ReplaceLatestGlobal:
                // Sender-agnostic single-entry bucket per (code, CacheKey).
                // The key names a shared logical slot; whichever peer writes
                // most recently owns it and any prior snapshot from any
                // other peer is evicted before the new one is appended.
                instance.Cache.RemoveByCodeAndKeyGlobal(msg.MessageCode, msg.CacheKey);
                instance.Cache.Add(senderPeerId, msg.MessageCode, msg.Delivery, msg.Channel, msg.Payload, msg.CacheKey);
                break;
        }
    }
}
