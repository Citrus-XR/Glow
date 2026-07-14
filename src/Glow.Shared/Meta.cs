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
// V4 protocol: PeerData is a byte-tagged namespace map (client picks
// arbitrary tags 0..255 with independent server-side byte quotas);
// SendMessage carries a CacheKey for CachePolicy.ReplaceLatest to
// dedupe per logical stream; HelloAck carries the server's build
// version string so clients can display / audit the peer they
// connected to.
public static class Meta
{
    public const string Name = "Glow";
    public const int ProtocolVersion = 4;

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
