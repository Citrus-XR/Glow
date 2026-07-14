using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// Hello: session hand-shake. Client may supply a DesiredUserId to keep
// identity across reconnects; otherwise the server allocates a fresh
// GUID-shaped one. Persisted PeerData for that user is loaded and echoed
// back so the client can rebuild its local mirror before joining.
public static class HelloHandler
{
    public static void Handle(GlowServer server, Session session, Hello msg)
    {
        if (msg.ProtocolVersion != Meta.ProtocolVersion)
        {
            server.SendError(session, 0, ErrorCode.ProtocolMismatch,
                $"Client protocol v{msg.ProtocolVersion}, server v{Meta.ProtocolVersion}");
            return;
        }

        var userId = !string.IsNullOrEmpty(msg.DesiredUserId)
            ? msg.DesiredUserId!
            : $"user-{Guid.NewGuid():N}";

        session.UserId = userId;
        session.PeerData = server.Persistence.LoadAll(userId);
        server.Send(session, new HelloAck(userId, server.Clock.NowMs, session.PeerData, Meta.BuildVersion));
    }
}
