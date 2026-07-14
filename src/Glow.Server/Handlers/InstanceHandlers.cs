using Glow.Server.Instances;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// JoinInstance / LeaveInstance + on-disconnect cleanup. Handles the
// full arrival sequence: peer allocation, PeerData push for existing
// peers, cached message replay, and PeerJoined broadcast.
public static class InstanceHandlers
{
    public static void HandleJoin(GlowServer server, Session session, JoinInstance msg)
    {
        if (session.UserId is null)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotAuthenticated, "Hello first.");
            return;
        }
        if (session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.AlreadyInInstance, "Session already in an instance.");
            return;
        }

        if (!server.Instances.TryGet(msg.InstanceName, out var instance))
        {
            if (msg.Mode == JoinMode.JoinOrCreate)
            {
                server.Instances.TryCreate(msg.InstanceName, out instance);
                instance.EmptyInstanceTtlMs = server.Options.EmptyInstanceTtlMs;
            }
            else
            {
                server.SendError(session, msg.RequestId, ErrorCode.InstanceNotFound,
                    $"Instance '{msg.InstanceName}' does not exist.");
                return;
            }
        }

        var result = instance.TryJoin(session.UserId, msg.Properties, msg.Mode, session.ConnectionId);
        if (result.ErrorCode != ErrorCode.Ok || result.Peer is null)
        {
            server.SendError(session, msg.RequestId, result.ErrorCode, "Join rejected.");
            return;
        }

        var peer = result.Peer;
        session.CurrentInstance = instance;
        session.CurrentPeer = peer;

        server.LogEvent(result.IsRejoin
            ? $"[Instance] '{instance.Name}' peer {peer.PeerId} ({peer.UserId}) rejoined, master={instance.MasterPeerId}, active={instance.ActivePeerCount}"
            : $"[Instance] '{instance.Name}' peer {peer.PeerId} ({peer.UserId}) joined, master={instance.MasterPeerId}, active={instance.ActivePeerCount}");

        // Apply optional atomic claim-at-join list. Each id is CAS-claimed
        // against "unowned"; already-owned ids silently skip. Doing this
        // before we build the response means the joiner's own snapshot
        // already reflects everything they successfully grabbed.
        List<(int NetworkId, int Previous, int Current)>? joinClaims = null;
        if (msg.ClaimObjectIds is { Length: > 0 } wants)
        {
            joinClaims = new List<(int, int, int)>(wants.Length);
            foreach (var nid in wants)
            {
                var (ok, prev, curr) = instance.TrySetObjectOwner(
                    nid, peer.PeerId, hasExpected: true, expected: 0);
                if (ok && prev != curr) joinClaims.Add((nid, prev, curr));
            }
            if (joinClaims.Count > 0)
                server.LogEvent($"[Instance] '{instance.Name}' peer {peer.PeerId} claimed {joinClaims.Count} object(s) at join: [{string.Join(",", joinClaims.Select(c => c.NetworkId))}]");
        }

        var peerIds = new int[instance.Peers.Count];
        var i = 0;
        foreach (var p in instance.Peers.Values) peerIds[i++] = p.PeerId;

        server.Send(session, new JoinInstanceAck(
            msg.RequestId,
            instance.Name,
            peer.PeerId,
            instance.MasterPeerId,
            peerIds,
            new Dictionary<string, PropertyValue>(instance.Properties),
            new Dictionary<int, int>(instance.ObjectOwners),
            server.Clock.NowMs));

        // Push each existing peer's PeerData snapshot to the newcomer.
        // This is how new clients rebuild remote state without a poll.
        // One PeerDataChanged per (peer, substore) so the receiver's
        // handler can dispatch by store tag exactly like a live mutation.
        foreach (var peerSession in server.InstanceSessions(instance))
        {
            if (peerSession.ConnectionId == session.ConnectionId) continue;
            if (peerSession.CurrentPeer is null) continue;
            foreach (var storeKv in peerSession.PeerData)
            {
                if (storeKv.Value.Count == 0) continue;
                server.Send(session, new PeerDataChanged(
                    peerSession.CurrentPeer.PeerId,
                    storeKv.Key,
                    new Dictionary<string, PropertyValue>(storeKv.Value)));
            }
        }

        // Replay cached messages in insertion order, on the original
        // sender's delivery + channel. Envelope is IncomingCachedMessage
        // (not IncomingMessage) so the receiver can distinguish replay
        // from a live send and react differently if needed.
        foreach (var cached in instance.Cache.Entries)
        {
            server.Send(session,
                new IncomingCachedMessage(cached.SenderPeerId, cached.MessageCode,
                    cached.Delivery, cached.Channel, cached.Payload),
                cached.Delivery.ToTransport(), cached.Channel);
        }

        if (!instance.SuppressJoinLeaveEvents)
        {
            // Deep-copy the newcomer's PeerData snapshot into the notify
            // payload so a later mutation on session.PeerData doesn't
            // leak into the frozen event queued for other peers.
            var pdCopy = new Dictionary<byte, Dictionary<string, PropertyValue>>(session.PeerData.Count);
            foreach (var kv in session.PeerData)
                pdCopy[kv.Key] = new Dictionary<string, PropertyValue>(kv.Value);
            var joinNotify = new PeerJoined(
                peer.PeerId,
                new Dictionary<string, PropertyValue>(peer.Properties),
                pdCopy);
            var others = new List<Session>();
            foreach (var s in server.InstanceSessions(instance))
                if (s.ConnectionId != session.ConnectionId) others.Add(s);
            server.Broadcast(others, joinNotify);

            // After peers know the newcomer exists, tell them which
            // object ids the newcomer just claimed. Own JoinInstanceAck
            // already contains the same info in its ObjectOwners map.
            if (joinClaims is { Count: > 0 })
            {
                foreach (var (nid, prev, curr) in joinClaims)
                    server.Broadcast(others, new ObjectOwnerChanged(nid, curr, prev));
            }
        }
    }

    public static void HandleLeave(GlowServer server, Session session, LeaveInstance msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        DoLeave(server, session, msg.BecomeInactive);
        server.Send(session, new LeaveInstanceAck(msg.RequestId));
    }

    // Called from GlowServer.OnDisconnected. Auto-inactive when PeerTtl > 0.
    public static void HandleDisconnect(GlowServer server, Session session) =>
        DoLeave(server, session, becomeInactive: session.CurrentInstance!.PeerTtlMs > 0);

    static void DoLeave(GlowServer server, Session session, bool becomeInactive)
    {
        var instance = session.CurrentInstance!;
        var peer = session.CurrentPeer!;
        var previousMaster = instance.MasterPeerId;

        if (becomeInactive)
            instance.MakePeerInactive(peer.PeerId, server.Clock.NowMs);
        else
            instance.RemovePeer(peer.PeerId, server.Clock.NowMs);

        session.CurrentInstance = null;
        session.CurrentPeer = null;

        List<(int NetworkId, int Previous, int Current)>? ownershipMoves = null;
        if (!becomeInactive)
            ownershipMoves = instance.TransferOwnershipFromPeer(peer.PeerId, instance.MasterPeerId);

        var newMaster = instance.MasterPeerId != previousMaster ? instance.MasterPeerId : 0;
        server.LogEvent(becomeInactive
            ? $"[Instance] '{instance.Name}' peer {peer.PeerId} ({peer.UserId}) went inactive, master={instance.MasterPeerId}, active={instance.ActivePeerCount}"
            : $"[Instance] '{instance.Name}' peer {peer.PeerId} ({peer.UserId}) left, master={instance.MasterPeerId}, active={instance.ActivePeerCount}");
        if (newMaster != 0)
            server.LogEvent($"[Instance] '{instance.Name}' master migrated {previousMaster} -> {newMaster}");
        if (ownershipMoves is { Count: > 0 })
        {
            foreach (var (nid, prev, curr) in ownershipMoves)
                server.LogEvent($"[Instance] '{instance.Name}' object {nid} owner {prev} -> {curr} (leave transfer)");
        }

        if (instance.SuppressJoinLeaveEvents) return;

        server.Broadcast(server.InstanceSessions(instance),
            new PeerLeft(peer.PeerId, becomeInactive, newMaster));

        if (ownershipMoves is { Count: > 0 })
        {
            foreach (var (nid, prev, curr) in ownershipMoves)
                server.Broadcast(server.InstanceSessions(instance),
                    new ObjectOwnerChanged(nid, curr, prev));
        }
    }
}
