using System.Threading.Channels;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

namespace Glow.Client;

// REPL + scripted driver over a ClientConnection.
public sealed class ReplHost
{
    readonly ClientConnection _conn = new();
    readonly ClientState _state = new();

    public ReplHost()
    {
        _conn.OnNotification += HandleNotification;
        _conn.OnLog += m => Console.WriteLine($"[net] {m}");
        _conn.OnDisconnected += info => Console.WriteLine($"[net] disconnected: {info.Reason}");
    }

    public async Task<int> RunInteractive(CancellationToken ct)
    {
        var inputs = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                await inputs.Writer.WriteAsync(line, ct).ConfigureAwait(false);
            }
            inputs.Writer.TryComplete();
        }, ct);

        Console.WriteLine("Glow client v3. Type `help` for commands, `quit` to exit.");
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await inputs.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }
            if (await Dispatch(line).ConfigureAwait(false) == false) break;
        }
        _conn.Disconnect();
        _conn.Dispose();
        return 0;
    }

    public async Task<int> RunScript(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"[client] script not found: {path}");
            return 2;
        }
        foreach (var raw in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) break;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Console.WriteLine($"> {line}");
            if (await Dispatch(line).ConfigureAwait(false) == false) break;
        }
        await Task.Delay(300, ct).ConfigureAwait(false);
        _conn.Disconnect();
        _conn.Dispose();
        return 0;
    }

    async Task<bool> Dispatch(string line)
    {
        var tokens = ValueParser.Tokenize(line);
        if (tokens.Count == 0) return true;
        var cmd = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToList();
        try
        {
            switch (cmd)
            {
                case "help": PrintHelp(); return true;
                case "quit" or "exit": return false;
                case "sleep": await DoSleep(args); return true;
                case "connect": await DoConnect(args); return true;
                case "disconnect": DoDisconnect(); return true;
                case "hello": await DoHello(args); return true;
                case "join": await DoJoin(args); return true;
                case "leave": await DoLeave(args); return true;
                case "send": DoSend(args, Routing.Others); return true;
                case "send-all": DoSend(args, Routing.All); return true;
                case "send-master": DoSend(args, Routing.Master); return true;
                case "send-to": DoSendTo(args); return true;
                case "send-group": DoSendGroup(args); return true;
                case "setprop": await DoSetProp(args, forPeer: false); return true;
                case "setpeerprop": await DoSetProp(args, forPeer: true); return true;
                case "getprops": await DoGetProps(args); return true;
                case "groups": DoGroups(args); return true;
                case "own": await DoOwn(args); return true;
                case "peerdata": await DoSetPeerData(args); return true;
                case "peerdata-get": await DoGetPeerData(); return true;
                case "state": PrintState(); return true;
                default: Console.WriteLine($"[client] unknown command: {cmd}"); return true;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] {cmd}: {ex.Message}");
            return true;
        }
    }

    static async Task DoSleep(List<string> args)
    {
        var ms = args.Count > 0 && int.TryParse(args[0], out var v) ? v : 250;
        await Task.Delay(ms).ConfigureAwait(false);
    }

    async Task DoConnect(List<string> args)
    {
        if (args.Count == 0) { Console.WriteLine("usage: connect host:port [key]"); return; }
        var parts = args[0].Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 1840;
        var key = args.Count > 1 ? args[1] : "glow";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ok = await _conn.ConnectAsync(host, port, key, cts.Token).ConfigureAwait(false);
        Console.WriteLine(ok ? "[client] connected" : "[client] connect failed");
    }

    void DoDisconnect() { _conn.Disconnect(); _state.Reset(); }

    async Task DoHello(List<string> args)
    {
        var msg = new Hello(Meta.ProtocolVersion, args.Count > 0 ? args[0] : null, null);
        // HelloAck arrives as notification (no RequestId in this protocol).
        var tcs = new TaskCompletionSource<HelloAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        void handler(Message m) { if (m is HelloAck a) tcs.TrySetResult(a); }
        _conn.OnNotification += handler;
        try
        {
            _conn.Fire(msg);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            var ack = await tcs.Task.ConfigureAwait(false);
            _state.UserId = ack.AssignedUserId;
            _state.PeerData = ack.PeerData;
            _state.LastServerTimeMs = ack.ServerTimeMs;
            _state.ServerBuildVersion = ack.ServerBuildVersion;
            PrintMessage(ack);
        }
        finally { _conn.OnNotification -= handler; }
    }

    async Task DoJoin(List<string> args)
    {
        if (args.Count == 0) { Console.WriteLine("usage: join <instance> [existing|create|rejoin] [--claim 1,2,3]"); return; }
        int[]? claim = null;
        var filtered = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--claim" && i + 1 < args.Count)
            {
                claim = args[++i].Split(',').Select(int.Parse).ToArray();
            }
            else
            {
                filtered.Add(args[i]);
            }
        }
        var name = filtered[0];
        var mode = filtered.Count > 1 ? filtered[1].ToLowerInvariant() switch
        {
            "create" => JoinMode.JoinOrCreate,
            "rejoin" => JoinMode.RejoinOnly,
            _ => JoinMode.JoinExisting,
        } : JoinMode.JoinExisting;
        var reqId = _conn.AllocateRequestId();
        var msg = new JoinInstance(reqId, name, mode, new Dictionary<string, PropertyValue>())
        {
            ClaimObjectIds = claim,
        };
        var resp = await _conn.SendRequest(reqId, msg).ConfigureAwait(false);
        PrintMessage(resp);
        if (resp is JoinInstanceAck a) ApplyJoin(a);
    }

    async Task DoLeave(List<string> args)
    {
        var inactive = args.Count > 0 && args[0].Equals("inactive", StringComparison.OrdinalIgnoreCase);
        var reqId = _conn.AllocateRequestId();
        var resp = await _conn.SendRequest(reqId, new LeaveInstance(reqId, inactive)).ConfigureAwait(false);
        PrintMessage(resp);
        if (resp is LeaveInstanceAck) _state.LeaveInstance();
    }

    // Parses trailing `--delivery <name>` and `--channel <n>` flags. Any
    // token that is neither a flag nor its value stays in `positional`.
    static (DeliveryMode Delivery, byte Channel, List<string> Positional) ExtractFlags(List<string> args)
    {
        var d = DeliveryMode.ReliableOrdered;
        byte ch = 0;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--delivery" && i + 1 < args.Count)
            {
                d = args[++i].ToLowerInvariant() switch
                {
                    "unreliable" or "u" => DeliveryMode.Unreliable,
                    "sequenced" or "s" => DeliveryMode.Sequenced,
                    "reliable" or "r" => DeliveryMode.Reliable,
                    "reliable-ordered" or "ro" => DeliveryMode.ReliableOrdered,
                    "reliable-sequenced" or "rs" => DeliveryMode.ReliableSequenced,
                    _ => throw new InvalidOperationException($"unknown delivery {args[i]}"),
                };
            }
            else if (args[i] == "--channel" && i + 1 < args.Count)
            {
                ch = byte.Parse(args[++i]);
            }
            else
            {
                positional.Add(args[i]);
            }
        }
        return (d, ch, positional);
    }

    void DoSend(List<string> args, Routing routing)
    {
        var (delivery, channel, positional) = ExtractFlags(args);
        if (positional.Count == 0) { Console.WriteLine("usage: send <code> [data] [--delivery X] [--channel N]"); return; }
        var code = (byte)int.Parse(positional[0]);
        var payload = positional.Count > 1 ? PayloadFromToken(positional[1]) : ReadOnlyMemory<byte>.Empty;
        _conn.Fire(new Shared.Messages.SendMessage(0, code, routing, null, 0, CachePolicy.None,
            delivery, channel, payload));
        Console.WriteLine($"[client] send code={code} routing={routing} delivery={delivery} ch={channel} bytes={payload.Length}");
    }

    void DoSendTo(List<string> args)
    {
        var (delivery, channel, positional) = ExtractFlags(args);
        if (positional.Count < 2) { Console.WriteLine("usage: send-to <a,b,c> <code> [data] [--delivery X] [--channel N]"); return; }
        var peers = positional[0].Split(',').Select(int.Parse).ToArray();
        var code = (byte)int.Parse(positional[1]);
        var payload = positional.Count > 2 ? PayloadFromToken(positional[2]) : ReadOnlyMemory<byte>.Empty;
        _conn.Fire(new Shared.Messages.SendMessage(0, code, Routing.Peers, peers, 0, CachePolicy.None,
            delivery, channel, payload));
        Console.WriteLine($"[client] send-to [{string.Join(",", peers)}] code={code} delivery={delivery} ch={channel}");
    }

    void DoSendGroup(List<string> args)
    {
        var (delivery, channel, positional) = ExtractFlags(args);
        if (positional.Count < 2) { Console.WriteLine("usage: send-group <n> <code> [data] [--delivery X] [--channel N]"); return; }
        var group = byte.Parse(positional[0]);
        var code = (byte)int.Parse(positional[1]);
        var payload = positional.Count > 2 ? PayloadFromToken(positional[2]) : ReadOnlyMemory<byte>.Empty;
        _conn.Fire(new Shared.Messages.SendMessage(0, code, Routing.Group, null, group, CachePolicy.None,
            delivery, channel, payload));
        Console.WriteLine($"[client] send-group {group} code={code} delivery={delivery} ch={channel}");
    }

    static ReadOnlyMemory<byte> PayloadFromToken(string token) =>
        System.Text.Encoding.UTF8.GetBytes(
            token.Length >= 2 && token[0] == '"' && token[^1] == '"' ? token[1..^1] : token);

    async Task DoSetProp(List<string> args, bool forPeer)
    {
        var offset = 0;
        var target = 0;
        if (forPeer)
        {
            if (args.Count < 2) { Console.WriteLine("usage: setpeerprop <peerId> k=v"); return; }
            target = int.Parse(args[0]);
            offset = 1;
        }
        if (args.Count <= offset) { Console.WriteLine("usage: setprop k=v"); return; }
        var kv = args[offset];
        var eq = kv.IndexOf('=');
        if (eq <= 0) { Console.WriteLine($"[client] bad kv: {kv}"); return; }
        var key = kv[..eq];
        var value = ValueParser.ParseValue(kv[(eq + 1)..]);
        var reqId = _conn.AllocateRequestId();
        var resp = await _conn.SendRequest(reqId,
            new SetProperty(reqId, target, key, value, HasExpected: false, Expected: PropertyValue.Null))
            .ConfigureAwait(false);
        PrintMessage(resp);
    }

    async Task DoGetProps(List<string> _)
    {
        var reqId = _conn.AllocateRequestId();
        var resp = await _conn.SendRequest(reqId,
            new GetProperties(reqId, IncludeInstance: true, IncludePeers: true, TargetPeers: null))
            .ConfigureAwait(false);
        PrintMessage(resp);
    }

    void DoGroups(List<string> args)
    {
        if (args.Count < 1) { Console.WriteLine("usage: groups add|remove <n> [n...]"); return; }
        var op = args[0].ToLowerInvariant();
        var ids = args.Skip(1).Select(byte.Parse).ToArray();
        _conn.Fire(op switch
        {
            "add" => new SubscribeGroups(ids, Array.Empty<byte>()),
            "remove" => new SubscribeGroups(Array.Empty<byte>(), ids),
            _ => throw new InvalidOperationException("first arg must be add or remove"),
        });
        Console.WriteLine($"[client] groups {op} {string.Join(",", ids)}");
    }

    async Task DoOwn(List<string> args)
    {
        if (args.Count == 0) { Console.WriteLine("usage: own <netId> [expected]"); return; }
        var nid = int.Parse(args[0]);
        var reqId = _conn.AllocateRequestId();
        var owner = _state.SelfPeerId ?? 0;
        var hasExpected = args.Count > 1;
        var expected = hasExpected ? int.Parse(args[1]) : 0;
        var resp = await _conn.SendRequest(reqId,
            new Shared.Messages.SetObjectOwner(reqId, nid, owner, hasExpected, expected))
            .ConfigureAwait(false);
        PrintMessage(resp);
    }

    async Task DoSetPeerData(List<string> args)
    {
        if (args.Count == 0) { Console.WriteLine("usage: peerdata [store=0|1] k=v [k=v...]"); return; }
        byte store = 0;
        var patch = new Dictionary<string, PropertyValue>();
        foreach (var kv in args)
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            var key = kv[..eq];
            if (key == "store")
            {
                if (!byte.TryParse(kv[(eq + 1)..], out store))
                {
                    Console.WriteLine($"invalid store byte: {kv[(eq + 1)..]}");
                    return;
                }
                continue;
            }
            patch[key] = ValueParser.ParseValue(kv[(eq + 1)..]);
        }
        var reqId = _conn.AllocateRequestId();
        var resp = await _conn.SendRequest(reqId, new Shared.Messages.SetPeerData(reqId, store, patch))
            .ConfigureAwait(false);
        PrintMessage(resp);
        if (resp is SetPeerDataAck ackOk && ackOk.ErrorCode == 0)
        {
            if (!_state.PeerData.TryGetValue(store, out var sub))
            {
                sub = new Dictionary<string, PropertyValue>();
                _state.PeerData[store] = sub;
            }
            foreach (var kv in patch)
            {
                if (kv.Value.IsNull) sub.Remove(kv.Key);
                else sub[kv.Key] = kv.Value;
            }
        }
    }

    async Task DoGetPeerData()
    {
        var reqId = _conn.AllocateRequestId();
        var resp = await _conn.SendRequest(reqId, new Shared.Messages.GetPeerData(reqId))
            .ConfigureAwait(false);
        PrintMessage(resp);
        if (resp is GetPeerDataAck a) _state.PeerData = a.Data;
    }

    void HandleNotification(Message m)
    {
        Console.WriteLine($"[<] {MessagePrinter.Format(m)}");
        switch (m)
        {
            case PeerJoined j: _state.KnownPeers.Add(j.PeerId); break;
            case PeerLeft l:
                _state.KnownPeers.Remove(l.PeerId);
                if (l.NewMasterPeerId != 0) _state.MasterPeerId = l.NewMasterPeerId;
                break;
            case ServerTime st: _state.LastServerTimeMs = st.ServerTimeMs; break;
            case Congestion c: _state.IsClogged = c.IsClogged; break;
            case ObjectOwnerChanged oc:
                if (oc.Current == 0) _state.ObjectOwners.Remove(oc.NetworkId);
                else _state.ObjectOwners[oc.NetworkId] = oc.Current;
                break;
        }
    }

    void ApplyJoin(JoinInstanceAck a)
    {
        _state.CurrentInstance = a.InstanceName;
        _state.SelfPeerId = a.MyPeerId;
        _state.MasterPeerId = a.MasterPeerId;
        _state.KnownPeers.Clear();
        foreach (var p in a.PeerIds) _state.KnownPeers.Add(p);
        _state.InstanceProperties.Clear();
        foreach (var kv in a.InstanceProperties) _state.InstanceProperties[kv.Key] = kv.Value;
        _state.ObjectOwners.Clear();
        foreach (var kv in a.ObjectOwners) _state.ObjectOwners[kv.Key] = kv.Value;
        _state.LastServerTimeMs = a.ServerTimeMs;
    }

    void PrintMessage(Message m) => Console.WriteLine($"[>] {MessagePrinter.Format(m)}");

    void PrintState()
    {
        Console.WriteLine("[client] state:");
        Console.WriteLine($"  UserId       = {_state.UserId ?? "(none)"}");
        Console.WriteLine($"  Instance     = {_state.CurrentInstance ?? "(none)"}");
        Console.WriteLine($"  SelfPeerId   = {(_state.SelfPeerId?.ToString() ?? "(none)")}");
        Console.WriteLine($"  MasterPeerId = {(_state.MasterPeerId?.ToString() ?? "(none)")}");
        Console.WriteLine($"  KnownPeers   = [{string.Join(",", _state.KnownPeers)}]");
        Console.WriteLine($"  ObjectOwners = {{{string.Join(",", _state.ObjectOwners.Select(kv => $"{kv.Key}={kv.Value}"))}}}");
        Console.WriteLine($"  ServerTimeMs = {(_state.LastServerTimeMs?.ToString() ?? "(none)")}");
        Console.WriteLine($"  IsClogged    = {_state.IsClogged}");
        Console.WriteLine($"  PeerData     = {{{string.Join(",", _state.PeerData.Select(s => $"s{s.Key}={{{string.Join(",", s.Value.Select(kv => $"{kv.Key}={kv.Value}"))}}}"))}}}");
        Console.WriteLine($"  Connected    = {_conn.IsConnected}");
    }

    static void PrintHelp() => Console.WriteLine(
        """
        Commands:
          connect <host:port> [key]        Connect
          disconnect                        Disconnect + clear state
          hello [userId]                    Hello handshake
          join <instance> [existing|create|rejoin] [--claim 1,2,3]
          leave [inactive]                  LeaveInstance
          send <code> [data] [--delivery X] [--channel N]
                                            SendMessage routing=Others
          send-all <code> [data] [--delivery X] [--channel N]
                                            SendMessage routing=All
          send-master <code> [data] [--delivery X] [--channel N]
                                            SendMessage routing=Master
          send-to <a,b,c> <code> [data] [--delivery X] [--channel N]
                                            SendMessage routing=Peers
          send-group <n> <code> [data] [--delivery X] [--channel N]
                                            SendMessage routing=Group

          --delivery: unreliable|sequenced|reliable|reliable-ordered|reliable-sequenced
                       (aliases: u / s / r / ro / rs; default: ro)
          --channel:   0..15 (default 0)

          setprop k=v                       SetProperty on the instance
          setpeerprop <peerId> k=v          SetProperty on a peer
          getprops                          GetProperties
          groups add|remove <n> [n...]      SubscribeGroups
          own <netId> [expected]            SetObjectOwner (CAS if expected given)
          peerdata [store=N] k=v [k=v...]   SetPeerData (store defaults to 0)
          peerdata-get                      GetPeerData (self)
          state                             Show local mirror
          sleep [ms]                        Wait (default 250)
          help / quit
        """);
}
