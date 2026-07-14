using System.Net;
using System.Net.Sockets;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using Glow.Shared.Wire;
using LiteNetLib;
using LiteNetLib.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Client
{
// LiteNetLib client speaking the Glow v3 wire protocol. A background poll
// task drives PollEvents; awaiting a request never blocks the pump. Each
// pending request is keyed by RequestId; the matching *Ack (or Error)
// resolves it. Send path is zero-copy: NetDataWriter is reused under a
// lock, LiteNetLib peer.Send takes the writer directly, no CopyData.
public sealed class ClientConnection : IDisposable
{
    readonly NetManager _net;
    readonly NetDataWriter _sendBuffer = new();
    readonly Dictionary<uint, TaskCompletionSource<Message>> _pending = new();
    readonly object _pendingLock = new();
    readonly CancellationTokenSource _pollCts = new();
    readonly Task _pollTask;
    NetPeer? _peer;
    uint _nextRequestId = 1;
    TaskCompletionSource<bool>? _connectTcs;

    public ClientConnection(byte channelsCount = 16, int updateIntervalMs = 5)
    {
        _net = new NetManager(new Listener(this))
        {
            AutoRecycle = true,
            ChannelsCount = channelsCount,
            UpdateTime = updateIntervalMs,
        };
        _net.Start();
        _pollTask = Task.Run(PollLoop);
    }

    public bool IsConnected => _peer is { ConnectionState: ConnectionState.Connected };
    public int? RoundTripTimeMs => _peer?.RoundTripTime;

    public event Action<Message>? OnNotification;
    public event Action<string>? OnLog;
    public event Action<DisconnectInfo>? OnDisconnected;

    public uint AllocateRequestId() { lock (_pendingLock) return _nextRequestId++; }

    public async Task<bool> ConnectAsync(string host, int port, string connectKey, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectTcs = tcs;
        _peer = _net.Connect(host, port, connectKey);
        using var reg = ct.Register(() => tcs.TrySetResult(false));
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var reg2 = timeoutCts.Token.Register(() => tcs.TrySetResult(false));
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Disconnect()
    {
        _peer?.Disconnect();
        _peer = null;
    }

    // Sends `message` and returns a Task that resolves with the paired
    // response (Ack or Error). Control messages (Hello / Join / property /
    // owner / peer-data) travel reliable-ordered on channel 0 by default;
    // callers that need something else use the overload below.
    public Task<Message> SendRequest(uint requestId, Message message) =>
        SendRequest(requestId, message, DeliveryMode.ReliableOrdered, channel: 0);

    public Task<Message> SendRequest(uint requestId, Message message, DeliveryMode delivery, byte channel)
    {
        var peer = _peer ?? throw new InvalidOperationException("Not connected.");
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[requestId] = tcs;
        var wire = delivery.ToTransport();
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, message);
            peer.Send(_sendBuffer, channel, wire);
        }
        return tcs.Task;
    }

    public void Fire(Message message) => Fire(message, DeliveryMode.ReliableOrdered, channel: 0);

    // Full-control fire. If the message is a SendMessage, its own
    // Delivery + Channel fields override; the outer LiteNetLib send uses
    // the same values so end-to-end semantics stay consistent.
    public void Fire(Message message, DeliveryMode delivery, byte channel)
    {
        var peer = _peer ?? throw new InvalidOperationException("Not connected.");
        if (message is SendMessage sm)
        {
            delivery = sm.Delivery;
            channel = sm.Channel;
        }
        var wire = delivery.ToTransport();
        lock (_sendBuffer)
        {
            _sendBuffer.Reset();
            MessageCodec.Write(_sendBuffer, message);
            peer.Send(_sendBuffer, channel, wire);
        }
    }

    public void Dispose()
    {
        _pollCts.Cancel();
        try { _pollTask.Wait(500); } catch { }
        _net.Stop();
        _pollCts.Dispose();
    }

    async Task PollLoop()
    {
        while (!_pollCts.IsCancellationRequested)
        {
            _net.PollEvents();
            try { await Task.Delay(15, _pollCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Resolves a pending request by RequestId. Ack messages resolve with
    // themselves; Error messages resolve with themselves too (caller can
    // pattern-match to detect failure).
    void CompletePending(uint requestId, Message message)
    {
        TaskCompletionSource<Message>? tcs = null;
        lock (_pendingLock)
            if (_pending.Remove(requestId, out var p)) tcs = p;
        tcs?.SetResult(message);
    }

    void HandleReceive(NetPacketReader reader)
    {
        try
        {
            var msg = MessageCodec.Read(reader);
            switch (msg)
            {
                case HelloAck a:
                    // HelloAck has no RequestId; if the caller used
                    // SendRequest they should key on Hello's own dummy id.
                    OnNotification?.Invoke(a);
                    break;
                case JoinInstanceAck a: CompletePending(a.RequestId, a); break;
                case LeaveInstanceAck a: CompletePending(a.RequestId, a); break;
                case SetPropertyAck a: CompletePending(a.RequestId, a); break;
                case GetPropertiesAck a: CompletePending(a.RequestId, a); break;
                case SetObjectOwnerAck a: CompletePending(a.RequestId, a); break;
                case SetPeerDataAck a: CompletePending(a.RequestId, a); break;
                case GetPeerDataAck a: CompletePending(a.RequestId, a); break;
                case Pong: /* ignore or expose separately */ break;
                case Error e when e.RequestId != 0: CompletePending(e.RequestId, e); break;
                default:
                    OnNotification?.Invoke(msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"decode error: {ex.Message}");
        }
    }

    void HandleConnected(NetPeer peer)
    {
        _connectTcs?.TrySetResult(true);
        OnLog?.Invoke($"connected id={peer.Id} rtt={peer.RoundTripTime}ms");
    }

    void HandleDisconnected(DisconnectInfo info)
    {
        _connectTcs?.TrySetResult(false);
        List<TaskCompletionSource<Message>> toFail;
        lock (_pendingLock)
        {
            toFail = new List<TaskCompletionSource<Message>>(_pending.Values);
            _pending.Clear();
        }
        foreach (var t in toFail) t.TrySetException(new IOException($"Disconnected: {info.Reason}"));
        _peer = null;
        OnDisconnected?.Invoke(info);
    }

    sealed class Listener : INetEventListener
    {
        readonly ClientConnection owner;
        public Listener(ClientConnection owner) { this.owner = owner; }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
        public void OnPeerConnected(NetPeer peer) => owner.HandleConnected(peer);
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) => owner.HandleDisconnected(info);
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery) =>
            owner.HandleReceive(reader);
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
            owner.OnLog?.Invoke($"network error {endPoint}: {socketError}");
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType type) { }
    }
}
}
