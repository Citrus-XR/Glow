using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// SetObjectOwner: server-serialized claim / release on a NetworkId. CAS
// via HasExpected + Expected. Anyone in the instance can claim any id;
// arrival order decides the winner. Broadcasts ObjectOwnerChanged on
// every actual value change.
public static class ObjectOwnerHandler
{
    public static void Handle(GlowServer server, Session session, Shared.Messages.SetObjectOwner msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, msg.RequestId, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        var instance = session.CurrentInstance!;
        var (success, previous, current) = instance.TrySetObjectOwner(
            msg.NetworkId, msg.NewOwner, msg.HasExpected, msg.Expected);
        if (!success)
        {
            server.SendError(session, msg.RequestId, ErrorCode.CasMismatch,
                $"Owner of {msg.NetworkId} is {previous}, expected {msg.Expected}.");
            return;
        }
        server.Send(session, new SetObjectOwnerAck(msg.RequestId, msg.NetworkId, current, previous));
        if (previous != current)
        {
            server.LogEvent($"[Instance] '{instance.Name}' object {msg.NetworkId} owner {previous} -> {current} (by peer {session.CurrentPeer!.PeerId})");
            server.Broadcast(server.InstanceSessions(instance),
                new ObjectOwnerChanged(msg.NetworkId, current, previous));
        }
    }
}
