using System.Net;
using System.Text;
using System.Text.Json;
using Glow.Server.Instances;

namespace Glow.Server;

// Read-only HTTP dump for debugging. Exposes /state as JSON. Kept
// deliberately minimal - no auth, no writes, no long-poll. Killed on
// dispose. Uses HttpListener because Kestrel's dependency tree is
// heavier than we need for a debug endpoint.
public sealed class StatusHttpServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly GlowServer _server;
    readonly CancellationTokenSource _cts = new();
    readonly string _prefix;
    Task? _acceptTask;

    public StatusHttpServer(GlowServer server, string prefix)
    {
        _prefix = prefix;
        _server = server;
        _listener.Prefixes.Add(prefix);
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoop);
        Console.WriteLine($"[status] listening on {_prefix}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptTask?.Wait(500); } catch { }
        _cts.Dispose();
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod != "GET")
            {
                Respond(ctx, 405, "text/plain", "Method not allowed"); return;
            }
            switch (path)
            {
                case "/":
                    Respond(ctx, 200, "text/plain",
                        $"Glow status. Endpoints: /state /version\n{Shared.Meta.Name} build {Shared.Meta.BuildVersion} | Protocol v{Shared.Meta.ProtocolVersion} | Server time {_server.Clock.NowMs} ms\n");
                    break;
                case "/version":
                    Respond(ctx, 200, "application/json", BuildVersionJson());
                    break;
                case "/state":
                    Respond(ctx, 200, "application/json", BuildStateJson());
                    break;
                default:
                    Respond(ctx, 404, "text/plain", "Not found"); break;
            }
        }
        catch (Exception ex)
        {
            try { Respond(ctx, 500, "text/plain", $"error: {ex.Message}"); } catch { }
        }
    }

    static void Respond(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        try { ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); }
        finally { ctx.Response.Close(); }
    }

    // Small dedicated endpoint for external tooling (dashboards, CI checks,
    // health probes) that only wants the identity + protocol tuple. Kept
    // separate from /state so a 2-second poll doesn't drag the full state
    // dump through the socket.
    string BuildVersionJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", Shared.Meta.Name);
            w.WriteString("buildVersion", Shared.Meta.BuildVersion);
            w.WriteNumber("protocolVersion", Shared.Meta.ProtocolVersion);
            w.WriteNumber("serverTimeMs", _server.Clock.NowMs);
            w.WriteNumber("boundPort", _server.Transport.BoundPort);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    string BuildStateJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", Shared.Meta.Name);
            w.WriteString("buildVersion", Shared.Meta.BuildVersion);
            w.WriteNumber("protocolVersion", Shared.Meta.ProtocolVersion);
            w.WriteNumber("serverTimeMs", _server.Clock.NowMs);
            w.WriteNumber("boundPort", _server.Transport.BoundPort);

            w.WriteStartArray("sessions");
            foreach (var s in _server.Transport.Sessions)
            {
                w.WriteStartObject();
                w.WriteNumber("connectionId", s.ConnectionId);
                w.WriteString("userId", s.UserId ?? "");
                w.WriteBoolean("isClogged", s.Outbound.IsClogged);
                w.WriteNumber("bytesInWindow", s.Outbound.BytesInWindow);
                w.WriteNumber("droppedUnreliableBytes", s.DroppedUnreliableBytes);
                w.WriteString("instance", s.CurrentInstance?.Name ?? "");
                w.WriteNumber("peerId", s.CurrentPeer?.PeerId ?? 0);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("instances");
            foreach (var kv in _server.Instances.All) WriteInstance(w, kv.Value);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteInstance(Utf8JsonWriter w, Instance instance)
    {
        w.WriteStartObject();
        w.WriteString("name", instance.Name);
        w.WriteNumber("masterPeerId", instance.MasterPeerId);
        w.WriteNumber("nextPeerId", instance.NextPeerId);
        w.WriteNumber("peerCount", instance.Peers.Count);
        w.WriteNumber("activeCount", instance.ActivePeerCount);
        w.WriteNumber("maxPeers", instance.MaxPeers);
        w.WriteBoolean("isOpen", instance.IsOpen);
        w.WriteBoolean("cleanupCacheOnLeave", instance.CleanupCacheOnLeave);

        w.WriteStartArray("peers");
        foreach (var p in instance.Peers.Values)
        {
            w.WriteStartObject();
            w.WriteNumber("peerId", p.PeerId);
            w.WriteString("userId", p.UserId);
            w.WriteBoolean("isActive", p.IsActive);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteNumber("cachedMessages", instance.Cache.Count);

        w.WriteStartObject("objectOwners");
        foreach (var kv in instance.ObjectOwners)
            w.WriteNumber(kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv.Value);
        w.WriteEndObject();

        w.WriteEndObject();
    }
}
