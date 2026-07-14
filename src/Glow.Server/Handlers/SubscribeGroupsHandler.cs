using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// SubscribeGroups. Add wins over Remove for the same group id; empty
// arrays are no-ops. Group 0 is permanent broadcast and cannot be
// unsubscribed. Successful subscribe produces no ack.
public static class SubscribeGroupsHandler
{
    public static void Handle(GlowServer server, Session session, SubscribeGroups msg)
    {
        if (!session.IsInInstance)
        {
            server.SendError(session, 0, ErrorCode.NotInInstance, "Not in an instance.");
            return;
        }
        var peer = session.CurrentPeer!;
        foreach (var g in msg.Remove) if (g != 0) peer.SubscribedGroups.Remove(g);
        foreach (var g in msg.Add) peer.SubscribedGroups.Add(g);
    }
}
