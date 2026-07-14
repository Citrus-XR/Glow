using Glow.Shared;

namespace Glow.Client;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? scriptPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--script" when i + 1 < args.Length: scriptPath = args[++i]; break;
                case "--help" or "-h": PrintHelp(); return 0;
                default:
                    Console.Error.WriteLine($"[client] unknown arg: {args[i]}");
                    PrintHelp(); return 2;
            }
        }
        Console.WriteLine($"{Meta.Name} Client v{Meta.ProtocolVersion}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var host = new ReplHost();
        return scriptPath is not null
            ? await host.RunScript(scriptPath, cts.Token).ConfigureAwait(false)
            : await host.RunInteractive(cts.Token).ConfigureAwait(false);
    }

    static void PrintHelp() => Console.WriteLine(
        """
        Glow Client v3
        Usage: Glow.Client [--script <path>] [--help]
        Interactive mode reads commands from stdin; type `help` for the list.
        """);
}
