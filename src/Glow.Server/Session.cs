using Glow.Shared;
using Glow.Shared.Protocol;

using Glow.Server.Bandwidth;
using Glow.Server.Instances;

namespace Glow.Server;

// Per-connection server-side state. A session progresses through
// Connected -> Authenticated -> InInstance. Handlers gate on the current
// state and reject with ErrorCode.NotAuthenticated / NotInInstance when
// a caller jumps a step.
public sealed class Session(int connectionId, int bandwidthBytesPerSecond)
{
    public int ConnectionId { get; } = connectionId;

    public string? UserId { get; set; }

    public Instance? CurrentInstance { get; set; }
    public Peer? CurrentPeer { get; set; }

    // Cached PeerData snapshot for this UserId, keyed by client-chosen
    // byte store tag. Copied out of the store on Hello, patched per-tag
    // by SetPeerData handlers, persisted on every mutation. Empty on
    // fresh sessions; tags materialize on first write.
    public Dictionary<byte, Dictionary<string, PropertyValue>> PeerData { get; set; } = new();

    public PerSecondBudget Outbound { get; } = new(bandwidthBytesPerSecond);
    public long DroppedUnreliableBytes { get; set; }

    public bool IsAuthenticated => UserId is not null;
    public bool IsInInstance => CurrentInstance is not null && CurrentPeer is not null;
}
