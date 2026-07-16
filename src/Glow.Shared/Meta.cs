using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared
{
// Wire protocol constants. Not derived from any prior networking library.
// V5 protocol: JoinInstanceAck now carries every existing peer's
// PeerData snapshot inline, so late joiners rebuild remote state
// atomically instead of waiting for a follow-up train of
// PeerDataChanged replays. V4 additions retained: PeerData is a
// byte-tagged namespace map with per-tag quotas; SendMessage carries
// a CacheKey for CachePolicy.ReplaceLatest to dedupe per logical
// stream; HelloAck carries the server's build version string so
// clients can display / audit the peer they connected to.
public static class Meta
{
    public const string Name = "Glow";
    public const int ProtocolVersion = 5;

    // Build version stamped by CI via -p:InformationalVersion. When
    // built locally without that flag the compiler falls back to the
    // project's <Version>, and if none is set the reflection call
    // returns "1.0.0+<commit-or-empty>". Never null.
    public static readonly string BuildVersion =
        typeof(Meta).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0-local";
}
}
