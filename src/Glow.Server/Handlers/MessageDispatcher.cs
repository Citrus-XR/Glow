using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Server.Handlers;

// Routes an incoming Message to the concrete handler. State-guard checks
// (authenticated / in-instance) live inside each handler so the dispatcher
// is a pure switch.
public sealed class MessageDispatcher(GlowServer server)
{
    public void Dispatch(Session session, Message message)
    {
        switch (message)
        {
            case Hello m: HelloHandler.Handle(server, session, m); break;

            case JoinInstance m: InstanceHandlers.HandleJoin(server, session, m); break;
            case LeaveInstance m: InstanceHandlers.HandleLeave(server, session, m); break;

            case Shared.Messages.SendMessage m: SendMessageHandler.Handle(server, session, m); break;

            case SetProperty m: PropertyHandlers.HandleSet(server, session, m); break;
            case GetProperties m: PropertyHandlers.HandleGet(server, session, m); break;

            case SubscribeGroups m: SubscribeGroupsHandler.Handle(server, session, m); break;

            case Shared.Messages.SetObjectOwner m: ObjectOwnerHandler.Handle(server, session, m); break;

            case Shared.Messages.SetPeerData m: PeerDataHandlers.HandleSet(server, session, m); break;
            case Shared.Messages.GetPeerData m: PeerDataHandlers.HandleGet(server, session, m); break;

            case Ping m: server.Send(session, new Pong(server.Clock.NowMs)); break;

            // Server-side messages should never arrive from a client.
            default:
                server.SendError(session, 0, ErrorCode.InvalidMessage,
                    $"Unexpected message type {message.Type}");
                break;
        }
    }
}
