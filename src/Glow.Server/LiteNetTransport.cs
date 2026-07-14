using System.Net;
using System.Net.Sockets;
using Glow.Shared.Messages;
using Glow.Shared.Wire;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Glow.Server;

// LiteNetLib wrapper. Owns the socket, decodes wire messages, and hands
// (Session, Message) pairs to a callback. All send paths route through
// here so the transport concern stays isolated from room logic.
public sealed class LiteNetTransport : INetEventListener
{
    readonly ServerOptions _options;
    readonly ServerClock _clock;
    readonly Action<Session, Message> _onMessage;
    readonly Action<Session> _onConnected;
    readonly Action<Session, DisconnectInfo> _onDisconnected;

    readonly Dictionary<int, Session> _sessions = [];
    readonly Dictionary<int, NetPeer> _peers = [];
    readonly NetManager _net;
    readonly NetDataWriter _sendBuffer = new();

    public LiteNetTransport(
        ServerOptions options,
        ServerClock clock,
        Action<Session, Message> onMessage,
        Action<Session> onConnected,
        Action<Session, DisconnectInfo> onDisconnected)
    {
        _options = options;
        _clock = clock;
        _onMessage = onMessage;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
        _net = new NetManager(this)
        {
            AutoRecycle = true,
            ChannelsCount = options.ChannelsCount,
            UpdateTime = options.TransportUpdateIntervalMs,
        };
    }

    public int BoundPort => _net.LocalPort;
    public IEnumerable<Session> Sessions => _sessions.Values;
    public bool TryGetSession(int connectionId, out Session session) =>
        _sessions.TryGetValue(connectionId, out session!);

    public void Start()
    {
        if (!_net.Start(_options.Port))
            throw new InvalidOperationException($"LiteNetTransport: failed to bind port {_options.Port}");
    }

    public void PollEvents() => _net.PollEvents();
    public void Stop() => _net.Stop();

    public void Send(Session session, Message message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered, byte channel = 0)
    {
        if (!_peers.TryGetValue(session.ConnectionId, out var peer)) return;
        _sendBuffer.Reset();
        MessageCodec.Write(_sendBuffer, message);
        var bytes = _sendBuffer.Length;
        if (delivery == DeliveryMethod.Unreliable && session.Outbound.IsClogged)
        {
            session.DroppedUnreliableBytes += bytes;
            return;
        }
        peer.Send(_sendBuffer, channel, delivery);
        session.Outbound.Record(_clock.NowMs, bytes);
    }

    public void Broadcast(IEnumerable<Session> targets, Message message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered, byte channel = 0)
    {
        _sendBuffer.Reset();
        MessageCodec.Write(_sendBuffer, message);
        var bytes = _sendBuffer.Length;
        var now = _clock.NowMs;
        foreach (var s in targets)
        {
            if (!_peers.TryGetValue(s.ConnectionId, out var peer)) continue;
            if (delivery == DeliveryMethod.Unreliable && s.Outbound.IsClogged)
            {
                s.DroppedUnreliableBytes += bytes;
                continue;
            }
            peer.Send(_sendBuffer, channel, delivery);
            s.Outbound.Record(now, bytes);
        }
    }

    void INetEventListener.OnConnectionRequest(ConnectionRequest request)
    {
        if (_options.ConnectKey.Length == 0) request.Accept();
        else request.AcceptIfKey(_options.ConnectKey);
    }

    void INetEventListener.OnPeerConnected(NetPeer peer)
    {
        var session = new Session(peer.Id, _options.PerSessionBytesPerSecond);
        _sessions[peer.Id] = session;
        _peers[peer.Id] = peer;
        _onConnected(session);
    }

    void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (_sessions.Remove(peer.Id, out var session))
            _onDisconnected(session, disconnectInfo);
        _peers.Remove(peer.Id);
    }

    void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        if (!_sessions.TryGetValue(peer.Id, out var session)) return;
        try
        {
            var message = MessageCodec.Read(reader);
            _onMessage(session, message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[transport] decode error from peer {peer.Id}: {ex.Message}");
            peer.Disconnect();
        }
    }

    void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        Console.Error.WriteLine($"[transport] network error at {endPoint}: {socketError}");

    void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
}
