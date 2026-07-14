using Glow.Server.Handlers;
using Glow.Server.Instances;
using Glow.Server.Persistence;
using Glow.Shared.Messages;
using LiteNetLib;

namespace Glow.Server;

// Top-level server. Owns transport, instance registry, clock, persistence.
// Callers drive Tick from their host loop; the server itself does not
// spawn threads beyond LiteNetLib's internals.
public sealed class GlowServer
{
    readonly ServerOptions _options;
    readonly LiteNetTransport _transport;
    readonly MessageDispatcher _dispatcher;
    readonly AdminHttpServer? _admin;
    long _lastServerTimeBroadcastMs;

    public GlowServer(ServerOptions options)
    {
        _options = options;
        Instances = new InstanceRegistry();
        Clock = new ServerClock();
        Persistence = new PeerDataStore(options.PeerDataDirectory, options.PeerDataStoreQuotaBytes);
        _transport = new LiteNetTransport(
            options, Clock,
            onMessage: OnMessage,
            onConnected: OnConnected,
            onDisconnected: OnDisconnected);
        _dispatcher = new MessageDispatcher(this);
        if (options.AdminHttpPrefix is not null)
            _admin = new AdminHttpServer(this, options.AdminHttpPrefix);
        if (options.DefaultInstanceName is not null)
        {
            Instances.TryCreate(options.DefaultInstanceName, out var defaultInstance);
            defaultInstance.EmptyInstanceTtlMs = options.EmptyInstanceTtlMs;
        }
    }

    public InstanceRegistry Instances { get; }
    public ServerClock Clock { get; }
    public PeerDataStore Persistence { get; }
    public LiteNetTransport Transport => _transport;
    public ServerOptions Options => _options;

    public void Start()
    {
        _transport.Start();
        Console.WriteLine($"[server] listening on UDP {_transport.BoundPort} (connect key: {_options.ConnectKey})");
        _admin?.Start();
    }

    public void Tick()
    {
        _transport.PollEvents();
        MaybeBroadcastServerTime();
        PollCongestion();
        SweepEmptyInstances();
    }

    public void Stop()
    {
        _admin?.Dispose();
        _transport.Stop();
    }

    // ---- Send helpers ---------------------------------------------

    public void SendError(Session session, uint requestId, short code, string debugMessage) =>
        _transport.Send(session, new Error(requestId, code, debugMessage));

    public void Send(Session session, Message message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered, byte channel = 0) =>
        _transport.Send(session, message, delivery, channel);

    public void Broadcast(IEnumerable<Session> targets, Message message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered, byte channel = 0) =>
        _transport.Broadcast(targets, message, delivery, channel);

    // Per-event logger, no-op when ServerOptions.Verbose is false. Startup
    // banners, shutdown, and error paths bypass this and write directly to
    // Console so they always surface even under --quiet.
    public void LogEvent(string message)
    {
        if (!_options.Verbose) return;
        Console.WriteLine(message);
    }

    // Enumerates active-peer sessions in an instance. Empty if no members.
    public IEnumerable<Session> InstanceSessions(Instance instance)
    {
        foreach (var peer in instance.ActivePeers)
        {
            if (peer.ConnectionId is int cid && _transport.TryGetSession(cid, out var session))
                yield return session;
        }
    }

    void OnConnected(Session session) =>
        LogEvent($"[server] peer connected id={session.ConnectionId}");

    void OnDisconnected(Session session, DisconnectInfo info)
    {
        LogEvent($"[server] peer disconnected id={session.ConnectionId} reason={info.Reason}");
        if (session.IsInInstance) InstanceHandlers.HandleDisconnect(this, session);
    }

    void OnMessage(Session session, Message message) => _dispatcher.Dispatch(session, message);

    void MaybeBroadcastServerTime()
    {
        var now = Clock.NowMs;
        if (now - _lastServerTimeBroadcastMs < _options.ServerTimeBroadcastIntervalMs) return;
        _lastServerTimeBroadcastMs = now;
        var msg = new ServerTime(now);
        _transport.Broadcast(_transport.Sessions, msg, DeliveryMethod.Unreliable, channel: 3);
    }

    void PollCongestion()
    {
        var now = Clock.NowMs;
        foreach (var session in _transport.Sessions)
        {
            if (!session.Outbound.Poll(now)) continue;
            _transport.Send(session, new Congestion(session.Outbound.IsClogged));
        }
    }

    void SweepEmptyInstances()
    {
        var removed = Instances.CleanupExpired(Clock.NowMs);
        if (removed.Count == 0) return;
        foreach (var name in removed)
            LogEvent($"[Instance] '{name}' destroyed after empty {_options.EmptyInstanceTtlMs}ms, NextPeerId reset for next join");
    }
}
