using Glow.Shared;

namespace Glow.Server;

public static class Program
{
    const string ConfigFileName = "glow.ini";

    public static async Task<int> Main(string[] args)
    {
        var configPath = Path.GetFullPath(ConfigFileName);
        var (options, configWasSeeded) = BuildOptions(args, configPath);

        var server = new GlowServer(options);
        server.Start();
        Console.WriteLine($"[server] {Meta.Name} v{Meta.ProtocolVersion} ready");
        if (configWasSeeded)
            Console.WriteLine($"[server] wrote default config to {configPath}");
        else
            Console.WriteLine($"[server] config loaded from {configPath}");
        PrintApplied(options);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("[server] shutdown requested");
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                server.Tick();
                await Task.Delay(15, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            server.Stop();
            Console.WriteLine("[server] stopped");
        }
        return 0;
    }

    // Precedence: CLI args > INI file > compile-time defaults. Missing
    // INI file is seeded from the compile-time defaults so subsequent
    // runs have a template to edit.
    static (ServerOptions Options, bool Seeded) BuildOptions(string[] args, string configPath)
    {
        var seeded = false;
        Dictionary<string, string> ini;
        if (File.Exists(configPath))
        {
            ini = ConfigFile.Read(configPath);
        }
        else
        {
            ConfigFile.Write(configPath, DefaultConfigEntries());
            ini = new Dictionary<string, string>(StringComparer.Ordinal);
            seeded = true;
        }

        // Layer 1: compile-time defaults from a fresh options record.
        var defaults = new ServerOptions();

        // Layer 2: INI-provided overrides.
        var port = GetInt(ini, "port", defaults.Port);
        var key = GetStr(ini, "key", defaults.ConnectKey);
        var instance = GetOptStr(ini, "instance", defaults.DefaultInstanceName);
        var status = GetOptStr(ini, "status", defaults.StatusHttpPrefix);
        var verbose = !GetBool(ini, "quiet", !defaults.Verbose);
        var peerDataDir = GetStr(ini, "peer-data-dir", defaults.PeerDataDirectory);
        var channels = (byte)GetInt(ini, "channels", defaults.ChannelsCount);
        var transportTickMs = GetInt(ini, "transport-tick-ms", defaults.TransportUpdateIntervalMs);
        var emptyTtlMs = GetInt(ini, "empty-instance-ttl-ms", defaults.EmptyInstanceTtlMs);
        var perSessionBps = GetInt(ini, "per-session-bps", defaults.PerSessionBytesPerSecond);
        var serverTimeMs = GetInt(ini, "server-time-broadcast-ms", defaults.ServerTimeBroadcastIntervalMs);
        var peerDataQuota = GetInt(ini, "peer-data-store-quota-bytes", defaults.PeerDataStoreQuotaBytes);

        // Layer 3: CLI overrides on top of the merged INI+default view.
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (arg)
            {
                case "--port" when next is not null: port = int.Parse(next); i++; break;
                case "--key" when next is not null: key = next; i++; break;
                case "--instance" when next is not null:
                    instance = next.Length == 0 ? null : next; i++; break;
                case "--no-instance": instance = null; break;
                case "--status" when next is not null:
                    status = next.Length == 0 ? null : next; i++; break;
                case "--no-status": status = null; break;
                case "--quiet" or "-q": verbose = false; break;
                case "--peer-data-dir" when next is not null: peerDataDir = next; i++; break;
                case "--channels" when next is not null: channels = byte.Parse(next); i++; break;
                case "--transport-tick-ms" when next is not null: transportTickMs = int.Parse(next); i++; break;
                case "--empty-instance-ttl-ms" when next is not null: emptyTtlMs = int.Parse(next); i++; break;
                case "--per-session-bps" when next is not null: perSessionBps = int.Parse(next); i++; break;
                case "--server-time-broadcast-ms" when next is not null: serverTimeMs = int.Parse(next); i++; break;
                case "--peer-data-store-quota-bytes" when next is not null: peerDataQuota = int.Parse(next); i++; break;
                case "--help" or "-h": PrintHelp(); Environment.Exit(0); break;
                default:
                    Console.Error.WriteLine($"[server] unknown arg: {arg}");
                    PrintHelp(); Environment.Exit(2); break;
            }
        }

        var options = new ServerOptions
        {
            Port = port,
            ConnectKey = key,
            DefaultInstanceName = instance,
            StatusHttpPrefix = status,
            Verbose = verbose,
            PeerDataDirectory = peerDataDir,
            ChannelsCount = channels,
            TransportUpdateIntervalMs = transportTickMs,
            EmptyInstanceTtlMs = emptyTtlMs,
            PerSessionBytesPerSecond = perSessionBps,
            ServerTimeBroadcastIntervalMs = serverTimeMs,
            PeerDataStoreQuotaBytes = peerDataQuota,
        };
        return (options, seeded);
    }

    // Order here also drives the layout of the seeded INI file, so keep
    // it grouped: connection basics first, then behavior, then tuning.
    static List<(string Key, string Value, string Comment)> DefaultConfigEntries()
    {
        var d = new ServerOptions();
        return new List<(string, string, string)>
        {
            ("port", d.Port.ToString(), "UDP port to bind."),
            ("key", d.ConnectKey, "Connect key clients must present in Hello."),
            ("instance", d.DefaultInstanceName ?? "",
                "Baseline instance created at startup. Leave empty to skip."),
            ("status", d.StatusHttpPrefix ?? "",
                "Status HTTP prefix (HttpListener). Leave empty to disable."),
            ("quiet", (!d.Verbose).ToString().ToLowerInvariant(),
                "true suppresses per-event logs; startup/errors always print."),
            ("peer-data-dir", d.PeerDataDirectory,
                "PeerData JSON store root. Relative paths resolve from the\nserver's working directory."),
            ("channels", d.ChannelsCount.ToString(),
                "LiteNetLib channel count. Same value required on the client."),
            ("transport-tick-ms", d.TransportUpdateIntervalMs.ToString(),
                "LiteNetLib internal logic thread tick (ms)."),
            ("empty-instance-ttl-ms", d.EmptyInstanceTtlMs.ToString(),
                "How long an empty instance survives before being destroyed:\n0 = immediate, >0 = grace ms, <0 = never destroy."),
            ("per-session-bps", d.PerSessionBytesPerSecond.ToString(),
                "Per-session outbound byte budget (bytes/sec)."),
            ("server-time-broadcast-ms", d.ServerTimeBroadcastIntervalMs.ToString(),
                "How often ServerTime is broadcast to all sessions (ms)."),
            ("peer-data-store-quota-bytes", d.PeerDataStoreQuotaBytes.ToString(),
                "Byte cap enforced per (user, store tag) in PeerData. Writes\nthat would cross it are rejected atomically."),
        };
    }

    static void PrintApplied(ServerOptions o)
    {
        Console.WriteLine("[server] applied options:");
        Console.WriteLine($"  port                       = {o.Port}");
        Console.WriteLine($"  key                        = {o.ConnectKey}");
        Console.WriteLine($"  instance                   = {o.DefaultInstanceName ?? "(none)"}");
        Console.WriteLine($"  status                     = {o.StatusHttpPrefix ?? "(disabled)"}");
        Console.WriteLine($"  quiet                      = {(!o.Verbose).ToString().ToLowerInvariant()}");
        Console.WriteLine($"  peer-data-dir              = {Path.GetFullPath(o.PeerDataDirectory)}");
        Console.WriteLine($"  channels                   = {o.ChannelsCount}");
        Console.WriteLine($"  transport-tick-ms          = {o.TransportUpdateIntervalMs}");
        Console.WriteLine($"  empty-instance-ttl-ms      = {o.EmptyInstanceTtlMs}");
        Console.WriteLine($"  per-session-bps            = {o.PerSessionBytesPerSecond}");
        Console.WriteLine($"  server-time-broadcast-ms   = {o.ServerTimeBroadcastIntervalMs}");
        Console.WriteLine($"  peer-data-store-quota-bytes= {o.PeerDataStoreQuotaBytes}");
    }

    static string GetStr(Dictionary<string, string> ini, string key, string fallback) =>
        ini.TryGetValue(key, out var v) ? v : fallback;

    // Empty string in INI means "unset" (null) for null-able settings.
    static string? GetOptStr(Dictionary<string, string> ini, string key, string? fallback)
    {
        if (!ini.TryGetValue(key, out var v)) return fallback;
        return v.Length == 0 ? null : v;
    }

    static int GetInt(Dictionary<string, string> ini, string key, int fallback) =>
        ini.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;

    static bool GetBool(Dictionary<string, string> ini, string key, bool fallback) =>
        ini.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    static void PrintHelp() => Console.WriteLine(
        """
        Glow Server v3

        Usage: Glow.Server [options]

        On first run a `glow.ini` is written to the working directory with
        all default values. Subsequent runs load that file; CLI flags below
        override individual keys.

        Options:
          --port <n>                     UDP port to bind
          --key <s>                      Connect key clients must present
          --instance <s>                 Baseline instance created at startup
          --no-instance                  Do not create a baseline instance
          --status <url>                 Status HTTP prefix
          --no-status                    Disable status HTTP listener
          --quiet, -q                    Suppress per-event logs
          --peer-data-dir <path>         PeerData JSON store root
          --channels <n>                 LiteNetLib channel count (byte)
          --transport-tick-ms <n>        LiteNetLib logic tick interval
          --empty-instance-ttl-ms <n>    Empty-instance destroy delay
          --per-session-bps <n>          Per-session outbound byte budget
          --server-time-broadcast-ms <n> ServerTime broadcast period
          --peer-data-store-quota-bytes <n>
                                         Byte cap per (user, store tag) in PeerData
          --help, -h                     Show this help
        """);
}
