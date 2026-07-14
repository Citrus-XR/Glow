namespace Glow.Server.Bandwidth;

// Fixed 1-second sliding window byte counter. Not a token bucket - the
// intent is not to rate-shape traffic, just to know when a session is
// consuming more bandwidth than the configured budget so the caller can
// drop unreliable events and notify the peer. The bucket resets on the
// first record call after the window elapses.
public sealed class PerSecondBudget(int bytesPerSecond)
{
    long _windowStartMs;
    int _bytesInWindow;

    public int BytesPerSecond { get; } = bytesPerSecond;

    public bool IsClogged { get; private set; }

    public int BytesInWindow => _bytesInWindow;

    // Records `bytes` outbound in the current window. Returns true when
    // the IsClogged state flipped as a result of this record - callers use
    // that as a trigger to notify the peer.
    public bool Record(long nowMs, int bytes)
    {
        RollWindow(nowMs);
        _bytesInWindow += bytes;
        var wasClogged = IsClogged;
        IsClogged = _bytesInWindow > BytesPerSecond;
        return wasClogged != IsClogged;
    }

    // Advances the window without adding traffic. Callers invoke this on a
    // tick loop so the flag can clear even when no send is happening.
    public bool Poll(long nowMs)
    {
        var wasClogged = IsClogged;
        RollWindow(nowMs);
        IsClogged = _bytesInWindow > BytesPerSecond;
        return wasClogged != IsClogged;
    }

    void RollWindow(long nowMs)
    {
        if (nowMs - _windowStartMs < 1000) return;
        _windowStartMs = nowMs;
        _bytesInWindow = 0;
    }
}
