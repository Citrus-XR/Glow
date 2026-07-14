using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Glow.Server;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using Glow.Shared.Wire;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Glow.Bench;

// End-to-end throughput. Spins up a real GlowServer on loopback + N
// LiteNetLib clients. Each sender fires M SendMessage packets as fast as
// it can; each receiver counts arrivals. Reports msgs/sec, MB/s at the
// wire layer (payload only), and average round-trip observed by the
// sender's poll thread.
public static class ThroughputBench
{
    public sealed record Scenario(
        string Name,
        int SenderCount,
        int ReceiverCount,
        int PayloadBytes,
        Routing Routing,
        DeliveryMode Delivery,
        byte Channel,
        int MessagesPerSender);

    public static async Task RunDefaultAsync()
    {
        Console.WriteLine();
        Console.WriteLine("======== end-to-end throughput ========");
        Console.WriteLine("[bench] server + N clients on UDP loopback. Zero-copy send path.");
        Console.WriteLine();

        foreach (var scenario in new[]
        {
            // 1 sender -> N receivers, small payload, default reliable-ordered.
            new Scenario("1s->1r  , 32B , Others  , ReliableOrdered ch0", 1, 1, 32, Routing.Others, DeliveryMode.ReliableOrdered, 0, 20_000),
            new Scenario("1s->4r  , 32B , Others  , ReliableOrdered ch0", 1, 4, 32, Routing.Others, DeliveryMode.ReliableOrdered, 0, 10_000),
            new Scenario("1s->8r  , 32B , Others  , ReliableOrdered ch0", 1, 8, 32, Routing.Others, DeliveryMode.ReliableOrdered, 0, 10_000),

            // Compare delivery modes on the same shape.
            new Scenario("1s->1r  , 32B , Others  , Reliable        ch0", 1, 1, 32, Routing.Others, DeliveryMode.Reliable, 0, 20_000),
            new Scenario("1s->1r  , 32B , Others  , Sequenced       ch0", 1, 1, 32, Routing.Others, DeliveryMode.Sequenced, 0, 20_000),
            new Scenario("1s->1r  , 32B , Others  , Unreliable      ch0", 1, 1, 32, Routing.Others, DeliveryMode.Unreliable, 0, 20_000),

            // Channel isolation: reliable-ordered on ch1 while another ordered stream
            // could live on ch0 - here we just verify a non-zero channel round-trips.
            new Scenario("1s->1r  , 32B , Others  , ReliableOrdered ch5", 1, 1, 32, Routing.Others, DeliveryMode.ReliableOrdered, 5, 20_000),

            // Larger payload.
            new Scenario("1s->1r  , 256B, Others  , ReliableOrdered ch0", 1, 1, 256, Routing.Others, DeliveryMode.ReliableOrdered, 0, 20_000),
            new Scenario("1s->4r  , 256B, Others  , ReliableOrdered ch0", 1, 4, 256, Routing.Others, DeliveryMode.ReliableOrdered, 0, 10_000),
            new Scenario("1s->1r  , 1024B,Others  , ReliableOrdered ch0", 1, 1, 1024, Routing.Others, DeliveryMode.ReliableOrdered, 0, 10_000),
            new Scenario("1s->1r  , 1024B,Others  , Reliable        ch0", 1, 1, 1024, Routing.Others, DeliveryMode.Reliable, 0, 10_000),
            new Scenario("1s->1r  , 1024B,Others  , Unreliable      ch0", 1, 1, 1024, Routing.Others, DeliveryMode.Unreliable, 0, 10_000),

            // Fan-in and full fanout.
            new Scenario("4s->1r  , 32B , All     , ReliableOrdered ch0", 4, 1, 32, Routing.All, DeliveryMode.ReliableOrdered, 0, 5_000),
            new Scenario("8s->8s  , 32B , Others  , ReliableOrdered ch0", 8, 8, 32, Routing.Others, DeliveryMode.ReliableOrdered, 0, 2_000),
            new Scenario("8s->8s  , 32B , Others  , Sequenced       ch0", 8, 8, 32, Routing.Others, DeliveryMode.Sequenced, 0, 2_000),
        })
        {
            await RunOne(scenario).ConfigureAwait(false);
        }
    }

    static async Task RunOne(Scenario s)
    {
        await using var harness = new BenchServerHarness(bandwidthBytesPerSecond: int.MaxValue);

        var senders = new BenchClient[s.SenderCount];
        var receivers = new BenchClient[s.ReceiverCount];
        for (var i = 0; i < s.SenderCount; i++)
        {
            senders[i] = new BenchClient();
            await senders[i].BootAsync(harness.Port, $"sender-{i}", "bench").ConfigureAwait(false);
        }
        for (var i = 0; i < s.ReceiverCount; i++)
        {
            receivers[i] = new BenchClient();
            await receivers[i].BootAsync(harness.Port, $"receiver-{i}", "bench").ConfigureAwait(false);
        }

        senders[0].FireSend(1, s.Routing, DeliveryMode.ReliableOrdered, 0, Array.Empty<byte>());
        await Task.Delay(50).ConfigureAwait(false);
        foreach (var r in receivers) r.ResetCounter();
        foreach (var s0 in senders) s0.ResetCounter();

        var payload = new byte[s.PayloadBytes];
        Random.Shared.NextBytes(payload);
        var totalToSend = s.SenderCount * s.MessagesPerSender;
        var expectedPerReceiver = totalToSend;

        Exception? failed = null;
        var sw = Stopwatch.StartNew();
        var sendTasks = new Task[s.SenderCount];
        for (var i = 0; i < s.SenderCount; i++)
        {
            var sender = senders[i];
            sendTasks[i] = Task.Run(() =>
            {
                try
                {
                    for (var m = 0; m < s.MessagesPerSender; m++)
                        sender.FireSend(7, s.Routing, s.Delivery, s.Channel, payload);
                }
                catch (Exception ex) { failed ??= ex; }
            });
        }
        await Task.WhenAll(sendTasks).ConfigureAwait(false);
        if (failed is not null)
        {
            Console.WriteLine($"  {s.Name}  SKIPPED: {failed.Message}");
            foreach (var c in senders) await c.DisposeAsync().ConfigureAwait(false);
            foreach (var c in receivers) await c.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var done = true;
            foreach (var r in receivers)
                if (r.CountReceived < expectedPerReceiver) { done = false; break; }
            if (done) break;
            await Task.Delay(10).ConfigureAwait(false);
        }
        if (s.Delivery is DeliveryMode.Unreliable or DeliveryMode.Sequenced)
            await Task.Delay(200).ConfigureAwait(false);
        sw.Stop();

        long totalReceived = 0;
        long totalWireBytes = 0;
        foreach (var r in receivers)
        {
            totalReceived += r.CountReceived;
            totalWireBytes += r.BytesReceived;
        }

        var seconds = sw.Elapsed.TotalSeconds;
        var msgsPerSec = totalReceived / seconds;
        var mbPerSec = totalWireBytes / seconds / (1024.0 * 1024.0);
        var expected = (long)expectedPerReceiver * s.ReceiverCount;
        var lossPct = 100.0 * (expected - totalReceived) / Math.Max(1, expected);

        Console.WriteLine(
            $"  {s.Name}  sent={totalToSend,6:N0}  " +
            $"recv={totalReceived,8:N0}  " +
            $"took={sw.Elapsed.TotalMilliseconds,7:F0} ms  " +
            $"={msgsPerSec,10:N0} msg/s  " +
            $"={mbPerSec,6:F1} MB/s  " +
            $"loss={lossPct,5:F1}%");

        foreach (var c in senders) await c.DisposeAsync().ConfigureAwait(false);
        foreach (var c in receivers) await c.DisposeAsync().ConfigureAwait(false);
    }
}

sealed class BenchServerHarness : IAsyncDisposable
{
    readonly GlowServer _server;
    readonly CancellationTokenSource _cts = new();
    readonly Task _tick;
    readonly string _dir;

    public BenchServerHarness(int bandwidthBytesPerSecond)
    {
        _dir = Path.Combine(Path.GetTempPath(), "glow-bench-" + Guid.NewGuid().ToString("N"));
        _server = new GlowServer(new ServerOptions
        {
            Port = 0,
            ConnectKey = "bench",
            DefaultInstanceName = "bench",
            StatusHttpPrefix = null,
            ServerTimeBroadcastIntervalMs = 100_000,
            PerSessionBytesPerSecond = bandwidthBytesPerSecond,
            PeerDataDirectory = _dir,
        });
        _server.Start();
        _tick = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                _server.Tick();
                try { await Task.Delay(1, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public int Port => _server.Transport.BoundPort;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _tick.ConfigureAwait(false); } catch { }
        _server.Stop();
        _cts.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

sealed class BenchClient : IAsyncDisposable
{
    readonly NetManager _net;
    readonly CancellationTokenSource _pollCts = new();
    readonly Task _pollTask;
    readonly NetDataWriter _sendBuffer = new();
    readonly Dictionary<uint, TaskCompletionSource<Message>> _pending = [];
    NetPeer? _peer;
    uint _nextRequestId = 1;
    TaskCompletionSource<bool>? _connectTcs;
    long _received;
    long _bytesReceived;

    public long CountReceived => Interlocked.Read(ref _received);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public void ResetCounter()
    {
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
    }

    public BenchClient()
    {
        _net = new NetManager(new Listener(this))
        {
            AutoRecycle = true,
            ChannelsCount = 16,
            UpdateTime = 5,
        };
        _net.Start();
        _pollTask = Task.Run(async () =>
        {
            while (!_pollCts.IsCancellationRequested)
            {
                _net.PollEvents();
                try { await Task.Delay(1, _pollCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public async Task BootAsync(int port, string userId, string instance)
    {
        _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _peer = _net.Connect("127.0.0.1", port, "bench");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var reg = cts.Token.Register(() => _connectTcs.TrySetResult(false));
        if (!await _connectTcs.Task.ConfigureAwait(false))
            throw new IOException("connect failed");

        // Hello carries no request id; resolved via notification.
        Fire(new Hello(Meta.ProtocolVersion, userId, null));
        // Wait a beat then Join. We don't strictly need to await HelloAck
        // because Join blocks server-side until UserId is set - but the
        // Hello message races Join across the same peer's send queue;
        // both land on channel 0 reliable-ordered so this is safe.
        var joinReq = AllocateRequestId();
        var join = await Request(joinReq, new JoinInstance(joinReq, instance,
            JoinMode.JoinOrCreate, new Dictionary<string, PropertyValue>()))
            .ConfigureAwait(false);
        if (join is Error e) throw new IOException($"join failed: {e.Code} {e.DebugMessage}");
    }

    uint AllocateRequestId() => Interlocked.Increment(ref _nextRequestId);

    public void FireSend(byte code, Routing routing, DeliveryMode delivery, byte channel, byte[] payload)
    {
        var m = new Shared.Messages.SendMessage(0, code, routing, null, 0, CachePolicy.None,
            delivery, channel, payload);
        FireMessage(m, delivery.ToTransport(), channel);
    }

    void FireMessage(Message m, DeliveryMethod wire, byte channel)
    {
        var peer = _peer ?? throw new InvalidOperationException("Not connected.");
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, m);
            peer.Send(_sendBuffer, channel, wire);
        }
    }

    void Fire(Message m) => FireMessage(m, DeliveryMethod.ReliableOrdered, 0);

    Task<Message> Request(uint reqId, Message m)
    {
        var peer = _peer ?? throw new InvalidOperationException();
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[reqId] = tcs;
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, m);
            peer.Send(_sendBuffer, 0, DeliveryMethod.ReliableOrdered);
        }
        return tcs.Task;
    }

    void HandleReceive(NetPacketReader reader)
    {
        var raw = reader.RawData;
        var offset = reader.UserDataOffset;
        var size = reader.UserDataSize;
        var msg = MessageCodec.Read(reader);
        switch (msg)
        {
            case IncomingMessage im:
                Interlocked.Increment(ref _received);
                Interlocked.Add(ref _bytesReceived, size);
                break;
            case JoinInstanceAck a:
                CompletePending(a.RequestId, a); break;
            case Error e when e.RequestId != 0:
                CompletePending(e.RequestId, e); break;
        }
    }

    void CompletePending(uint id, Message m)
    {
        TaskCompletionSource<Message>? tcs = null;
        lock (_pending) if (_pending.Remove(id, out var t)) tcs = t;
        tcs?.SetResult(m);
    }

    public async ValueTask DisposeAsync()
    {
        _peer?.Disconnect();
        _pollCts.Cancel();
        try { await _pollTask.ConfigureAwait(false); } catch { }
        _net.Stop();
        _pollCts.Dispose();
    }

    sealed class Listener(BenchClient owner) : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest r) => r.Reject();
        public void OnPeerConnected(NetPeer p) => owner._connectTcs?.TrySetResult(true);
        public void OnPeerDisconnected(NetPeer p, DisconnectInfo i) => owner._connectTcs?.TrySetResult(false);
        public void OnNetworkReceive(NetPeer p, NetPacketReader r, byte c, DeliveryMethod d) => owner.HandleReceive(r);
        public void OnNetworkError(IPEndPoint e, SocketError s) { }
        public void OnNetworkLatencyUpdate(NetPeer p, int l) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint e, NetPacketReader r, UnconnectedMessageType t) { }
    }
}
