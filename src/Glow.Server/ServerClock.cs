using System.Diagnostics;

namespace Glow.Server;

// Monotonic server clock. A raw 32-bit millisecond tick wraps every
// ~24.9 days, which is short enough to bite a long-running server;
// Glow's primary NowMs is 64-bit via Stopwatch so it never wraps within
// a server lifetime. Clients receive the current value in the
// ServerTime event and compute their local offset once.
public sealed class ServerClock
{
    readonly Stopwatch _sw = Stopwatch.StartNew();

    public long NowMs => _sw.ElapsedMilliseconds;

    // Optional 32-bit tick view for callers that need a compact,
    // wire-cheap counter. Wraps naturally when the server has run past
    // 2^31 ms; anything requiring monotonicity should use NowMs.
    public int TickCount32 => unchecked((int)_sw.ElapsedMilliseconds);
}
