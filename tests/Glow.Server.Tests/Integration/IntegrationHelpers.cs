using System.Net;
using System.Net.Sockets;
using Glow.Server;
using Glow.Shared.Messages;
using Glow.Shared.Wire;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Glow.Server.Tests.Integration;

// Boots a live GlowServer on an ephemeral UDP port with its Tick loop on
// a background task. Per-run PeerData directory so parallel test runs
// don't interfere.
public sealed class ServerHarness : IAsyncDisposable
{
    readonly GlowServer _server;
    readonly CancellationTokenSource _cts = new();
    readonly Task _tickTask;
    readonly string _tempDir;

    public ServerHarness(int? bandwidthBytesPerSecond = null)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glow-it-" + Guid.NewGuid().ToString("N"));
        _server = new GlowServer(new ServerOptions
        {
            Port = 0,
            ConnectKey = "glow",
            DefaultInstanceName = "test-instance",
            StatusHttpPrefix = null,
            ServerTimeBroadcastIntervalMs = 100000,
            PerSessionBytesPerSecond = bandwidthBytesPerSecond ?? 11 * 1024,
            PeerDataDirectory = _tempDir,
        });
        _server.Start();
        _tickTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                _server.Tick();
                try { await Task.Delay(10, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public int Port => _server.Transport.BoundPort;
    public GlowServer Server => _server;
    public string PeerDataDirectory => _tempDir;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _tickTask.ConfigureAwait(false); } catch { }
        _server.Stop();
        _cts.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}

// LiteNetLib client for tests. Records every message as it arrives so
// assertions can scan history. Requests keyed by RequestId; matching
// Ack / Error resolves the pending TCS.
public sealed class TestClient : IAsyncDisposable
{
    readonly NetManager _net;
    readonly CancellationTokenSource _pollCts = new();
    readonly Task _pollTask;
    readonly NetDataWriter _sendBuffer = new();
    readonly Dictionary<uint, TaskCompletionSource<Message>> _pending = [];
    readonly object _pendingLock = new();
    readonly List<Message> _received = [];
    readonly object _receivedLock = new();
    NetPeer? _peer;
    uint _nextRequestId = 1;
    TaskCompletionSource<bool>? _connectTcs;

    public TestClient()
    {
        _net = new NetManager(new Listener(this)) { AutoRecycle = true, ChannelsCount = 16 };
        _net.Start();
        _pollTask = Task.Run(PollLoop);
    }

    public bool IsConnected => _peer is { ConnectionState: ConnectionState.Connected };

    public IReadOnlyList<Message> Received
    {
        get { lock (_receivedLock) return _received.ToArray(); }
    }

    public int ReceivedCount { get { lock (_receivedLock) return _received.Count; } }

    public uint AllocateRequestId() { lock (_pendingLock) return _nextRequestId++; }

    public async Task<bool> ConnectAsync(int port, string key = "glow", int timeoutMs = 4000)
    {
        _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _peer = _net.Connect("127.0.0.1", port, key);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(() => _connectTcs.TrySetResult(false));
        return await _connectTcs.Task.ConfigureAwait(false);
    }

    public void Disconnect() { _peer?.Disconnect(); _peer = null; }

    public Task<Message> Request(uint requestId, Message message)
    {
        var peer = _peer ?? throw new InvalidOperationException("Not connected.");
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[requestId] = tcs;
        byte[] payload;
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, message);
            payload = _sendBuffer.CopyData();
        }
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
        return tcs.Task;
    }

    public void Fire(Message message)
    {
        var peer = _peer ?? throw new InvalidOperationException("Not connected.");
        byte[] payload;
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, message);
            payload = _sendBuffer.CopyData();
        }
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
    }

    // Polling waiters. 15 ms polling is fine on loopback.
    public async Task<T> WaitFor<T>(int timeoutMs = 3000, int fromIndex = 0) where T : Message
    {
        if (fromIndex < 0) fromIndex = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_receivedLock)
            {
                for (var i = fromIndex; i < _received.Count; i++)
                    if (_received[i] is T t) return t;
            }
            await Task.Delay(15).ConfigureAwait(false);
        }
        throw new TimeoutException($"No {typeof(T).Name} observed within {timeoutMs} ms.");
    }

    public async Task<T> WaitFor<T>(Func<T, bool> predicate, int timeoutMs = 3000) where T : Message
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_receivedLock)
                foreach (var m in _received)
                    if (m is T t && predicate(t)) return t;
            await Task.Delay(15).ConfigureAwait(false);
        }
        throw new TimeoutException($"No matching {typeof(T).Name} within {timeoutMs} ms.");
    }

    public async ValueTask DisposeAsync()
    {
        _pollCts.Cancel();
        try { await _pollTask.ConfigureAwait(false); } catch { }
        _net.Stop();
        _pollCts.Dispose();
    }

    async Task PollLoop()
    {
        while (!_pollCts.IsCancellationRequested)
        {
            _net.PollEvents();
            try { await Task.Delay(10, _pollCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    void HandleReceive(NetPacketReader reader)
    {
        try
        {
            var m = MessageCodec.Read(reader);
            lock (_receivedLock) _received.Add(m);
            var requestId = MessageRequestId(m);
            if (requestId != 0)
            {
                TaskCompletionSource<Message>? tcs = null;
                lock (_pendingLock)
                    if (_pending.Remove(requestId, out var p)) tcs = p;
                tcs?.SetResult(m);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[test-client] decode error: {ex.Message}");
        }
    }

    static uint MessageRequestId(Message m) => m switch
    {
        JoinInstanceAck a => a.RequestId,
        LeaveInstanceAck a => a.RequestId,
        SetPropertyAck a => a.RequestId,
        GetPropertiesAck a => a.RequestId,
        SetObjectOwnerAck a => a.RequestId,
        SetPeerDataAck a => a.RequestId,
        GetPeerDataAck a => a.RequestId,
        Error e => e.RequestId,
        _ => 0,
    };

    void HandleConnected() => _connectTcs?.TrySetResult(true);
    void HandleDisconnected() => _connectTcs?.TrySetResult(false);

    sealed class Listener(TestClient owner) : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest r) => r.Reject();
        public void OnPeerConnected(NetPeer p) => owner.HandleConnected();
        public void OnPeerDisconnected(NetPeer p, DisconnectInfo i) => owner.HandleDisconnected();
        public void OnNetworkReceive(NetPeer p, NetPacketReader r, byte c, DeliveryMethod d) => owner.HandleReceive(r);
        public void OnNetworkError(IPEndPoint e, SocketError s) { }
        public void OnNetworkLatencyUpdate(NetPeer p, int l) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint e, NetPacketReader r, UnconnectedMessageType t) { }
    }
}
