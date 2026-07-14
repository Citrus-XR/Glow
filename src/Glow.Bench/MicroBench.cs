using System.Diagnostics;
using System.Text;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using Glow.Shared.Wire;
using LiteNetLib.Utils;

namespace Glow.Bench;

// Hand-rolled micro-benchmarks. Each case runs a warmup pass to prime
// caches / JIT, then a timed pass at a target iteration count. Reports
// ns/op, ops/sec, and (where meaningful) MB/s of wire throughput.
public static class MicroBench
{
    public static void RunAll()
    {
        Console.WriteLine();
        Console.WriteLine("======== micro-benchmarks ========");
        WireCodecs();
        PropertyValueOps();
    }

    static void WireCodecs()
    {
        // Fresh-allocation cost (single message per iteration)
        BenchWire("Hello encode+decode (fresh writer)", 500_000,
            () => new Hello(2, "alice", "some-token"),
            fresh: true);

        BenchWire("HelloAck encode+decode (fresh writer, 5-key PeerData in one store)", 300_000,
            () => new HelloAck("alice-user", 1234567890L, new Dictionary<byte, Dictionary<string, PropertyValue>>
            {
                [0] = new Dictionary<string, PropertyValue>
                {
                    ["score"] = PropertyValue.From(100),
                    ["mode"] = PropertyValue.From("match"),
                    ["ready"] = PropertyValue.From(true),
                    ["alpha"] = PropertyValue.From(3.14f),
                    ["blob"] = PropertyValue.From(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
                },
            }, "bench-build"),
            fresh: true);

        BenchWire("JoinInstanceAck encode+decode (3 peers, 4 props, 2 owners)", 200_000,
            () => new JoinInstanceAck(
                42, "test-room", 3, 1,
                new[] { 1, 2, 3 },
                new Dictionary<string, PropertyValue>
                {
                    ["mode"] = PropertyValue.From("free"),
                    ["max"] = PropertyValue.From(16),
                    ["team1"] = PropertyValue.From("red"),
                    ["team2"] = PropertyValue.From("blue"),
                },
                new Dictionary<int, int> { [100] = 1, [200] = 2 },
                123456L),
            fresh: true);

        var smallPayload = Encoding.UTF8.GetBytes("hello");
        BenchWire("SendMessage encode+decode (small 5B payload)", 1_000_000,
            () => new Shared.Messages.SendMessage(1, 42, Routing.Others, null, 0,
                CachePolicy.None, DeliveryMode.ReliableOrdered, 0, smallPayload),
            fresh: true);

        var bigPayload = new byte[1024];
        BenchWire("SendMessage encode+decode (1KB payload)", 500_000,
            () => new Shared.Messages.SendMessage(1, 42, Routing.All, null, 0,
                CachePolicy.None, DeliveryMode.ReliableOrdered, 0, bigPayload),
            fresh: true);

        // Reusable writer path (best-case, mimics steady-state server broadcast)
        BenchWireReused("SendMessage encode+decode (1KB, reused writer)", 1_000_000,
            new Shared.Messages.SendMessage(1, 42, Routing.All, null, 0,
                CachePolicy.None, DeliveryMode.ReliableOrdered, 0, bigPayload));
    }

    static void BenchWire(string name, int iters, Func<Message> factory, bool fresh)
    {
        // Warmup
        for (var i = 0; i < Math.Min(iters, 10_000); i++)
        {
            var m = factory();
            var w = new NetDataWriter();
            MessageCodec.Write(w, m);
            var r = new NetDataReader(w.CopyData());
            MessageCodec.Read(r);
        }

        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iters; i++)
        {
            var m = factory();
            var w = new NetDataWriter();
            MessageCodec.Write(w, m);
            totalBytes += w.Length;
            var r = new NetDataReader(w.CopyData());
            MessageCodec.Read(r);
        }
        sw.Stop();
        Report(name, iters, sw.Elapsed, totalBytes);
    }

    static void BenchWireReused(string name, int iters, Message m)
    {
        var writer = new NetDataWriter();
        // Warmup
        for (var i = 0; i < 10_000; i++)
        {
            writer.Reset();
            MessageCodec.Write(writer, m);
            var r = new NetDataReader(writer.CopyData());
            MessageCodec.Read(r);
        }
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iters; i++)
        {
            writer.Reset();
            MessageCodec.Write(writer, m);
            totalBytes += writer.Length;
            var r = new NetDataReader(writer.CopyData());
            MessageCodec.Read(r);
        }
        sw.Stop();
        Report(name, iters, sw.Elapsed, totalBytes);
    }

    static void PropertyValueOps()
    {
        var pv1 = PropertyValue.From(42);
        var pv2 = PropertyValue.From(42);
        var pv3 = PropertyValue.From("hello world");
        var pv4 = PropertyValue.From("hello world");

        BenchOp("PropertyValue.Equals (int, hit)", 50_000_000, () => pv1.Equals(pv2));
        BenchOp("PropertyValue.Equals (string, hit)", 20_000_000, () => pv3.Equals(pv4));
        BenchOp("PropertyValue.From(int) + Equals", 20_000_000, () =>
            PropertyValue.From(42).Equals(PropertyValue.From(42)));
        BenchOp("PropertyValue.From(bytes) + Equals (8B)", 5_000_000, () =>
        {
            var a = PropertyValue.From(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var b = PropertyValue.From(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            return a.Equals(b);
        });
    }

    static void BenchOp(string name, int iters, Func<bool> body)
    {
        for (var i = 0; i < 100_000; i++) body();
        var sw = Stopwatch.StartNew();
        var acc = 0;
        for (var i = 0; i < iters; i++) if (body()) acc++;
        sw.Stop();
        Report(name, iters, sw.Elapsed, totalBytes: 0, verify: acc == iters);
    }

    static void Report(string name, long iters, TimeSpan elapsed, long totalBytes, bool verify = true)
    {
        var nsPerOp = elapsed.TotalNanoseconds / iters;
        var opsPerSec = iters / elapsed.TotalSeconds;
        var bytesPerSec = totalBytes > 0 ? totalBytes / elapsed.TotalSeconds : 0;
        var mbPerSec = bytesPerSec / (1024.0 * 1024.0);
        Console.WriteLine(
            $"  {name,-70}  {nsPerOp,10:F1} ns/op  " +
            $"{opsPerSec,15:N0} ops/s" +
            (bytesPerSec > 0 ? $"  {mbPerSec,7:F1} MB/s" : "") +
            (verify ? "" : " [!verify]"));
    }
}
