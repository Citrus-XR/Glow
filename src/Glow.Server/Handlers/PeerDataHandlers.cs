using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// SetPeerData / GetPeerData. Patches merge into the caller's substore
// dictionary keyed by (UserId, Store) (null value deletes). Quota is checked
// per slash-delimited key namespace. Over-quota
// writes are rejected whole with SetPeerDataAck.ErrorCode = QuotaExceeded
// and never persisted or broadcast. Successful mutations are broadcast
// to the current instance as PeerDataChanged carrying the same store
// tag so peers can mirror it without polling.
public static class PeerDataHandlers
{
    public static void HandleSet(GlowServer server, Session session, Shared.Messages.SetPeerData msg)
    {
        if (session.UserId is null)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotAuthenticated, "Hello first.");
            return;
        }

        var (ok, snapshot) = server.Persistence.Merge(session.UserId, msg.Store, msg.Patch);
        session.PeerData[msg.Store] = snapshot;

        if (!ok)
        {
            // Silent-drop semantics on the wire: caller gets an Ack with
            // the error code set; nothing is broadcast to the instance.
            server.Send(session, new SetPeerDataAck(msg.RequestId, ErrorCode.QuotaExceeded));
            return;
        }

        server.Send(session, new SetPeerDataAck(msg.RequestId, ErrorCode.Ok));

        if (session.IsInInstance)
        {
            var evt = new PeerDataChanged(session.CurrentPeer!.PeerId, msg.Store, msg.Patch);
            var others = new List<Session>();
            foreach (var s in server.InstanceSessions(session.CurrentInstance!))
                if (s.ConnectionId != session.ConnectionId) others.Add(s);
            server.Broadcast(others, evt);
        }
    }

    public static void HandleGet(GlowServer server, Session session, Shared.Messages.GetPeerData msg)
    {
        if (session.UserId is null)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotAuthenticated, "Hello first.");
            return;
        }
        // Fresh copy per substore so the caller can't mutate our cache.
        var snapshot = new Dictionary<byte, Dictionary<string, PropertyValue>>(session.PeerData.Count);
        foreach (var kv in session.PeerData)
            snapshot[kv.Key] = new Dictionary<string, PropertyValue>(kv.Value);
        server.Send(session, new GetPeerDataAck(msg.RequestId, snapshot));
    }
}
