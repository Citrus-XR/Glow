using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// SetProperty + GetProperties. TargetPeerId == 0 addresses instance
// properties; otherwise a specific peer. CAS is optional per request.
public static class PropertyHandlers
{
    public static void HandleSet(GlowServer server, Session session, SetProperty msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        if (msg.Key.Length == 0)
        {
            server.SendError(session, msg.RequestId, ErrorCode.PropertyKeyInvalid, "Property key required.");
            return;
        }

        var instance = session.CurrentInstance!;
        Dictionary<string, PropertyValue> target;
        if (msg.TargetPeerId == 0)
        {
            target = instance.Properties;
        }
        else
        {
            if (!instance.Peers.TryGetValue(msg.TargetPeerId, out var p))
            {
                server.SendError(session, msg.RequestId, ErrorCode.PropertyTargetMissing,
                    $"Peer {msg.TargetPeerId} not found in instance.");
                return;
            }
            target = p.Properties;
        }

        if (msg.HasExpected)
        {
            var current = target.TryGetValue(msg.Key, out var cur) ? cur : PropertyValue.Null;
            if (!current.Equals(msg.Expected))
            {
                server.SendError(session, msg.RequestId, ErrorCode.CasMismatch,
                    $"CAS mismatch on '{msg.Key}'.");
                return;
            }
        }

        if (msg.Value.IsNull) target.Remove(msg.Key);
        else target[msg.Key] = msg.Value;

        server.Send(session, new SetPropertyAck(msg.RequestId));

        var evt = new PropertyChanged(msg.TargetPeerId, msg.Key, msg.Value, session.CurrentPeer!.PeerId);
        var targets = new List<Session>();
        foreach (var s in server.InstanceSessions(instance))
        {
            if (!instance.BroadcastPropertyChangeToAll && s.ConnectionId == session.ConnectionId)
                continue;
            targets.Add(s);
        }
        server.Broadcast(targets, evt);
    }

    public static void HandleGet(GlowServer server, Session session, GetProperties msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        var instance = session.CurrentInstance!;
        var instanceProps = msg.IncludeInstance
            ? new Dictionary<string, PropertyValue>(instance.Properties)
            : [];
        var peerProps = new Dictionary<int, Dictionary<string, PropertyValue>>();
        if (msg.IncludePeers)
        {
            foreach (var p in instance.Peers.Values)
            {
                if (msg.TargetPeers is not null && Array.IndexOf(msg.TargetPeers, p.PeerId) < 0)
                    continue;
                peerProps[p.PeerId] = new Dictionary<string, PropertyValue>(p.Properties);
            }
        }
        server.Send(session, new GetPropertiesAck(msg.RequestId, instanceProps, peerProps));
    }
}
