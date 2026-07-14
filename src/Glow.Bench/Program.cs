namespace Glow.Bench;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var runMicro = args.Length == 0 || args.Contains("--micro") || args.Contains("--all");
        var runThroughput = args.Length == 0 || args.Contains("--throughput") || args.Contains("--all");

        if (runMicro) MicroBench.RunAll();
        if (runThroughput) await ThroughputBench.RunDefaultAsync().ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("[bench] done");
        return 0;
    }
}
