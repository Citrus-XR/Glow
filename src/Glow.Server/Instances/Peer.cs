using Glow.Shared;
using Glow.Shared.Protocol;

namespace Glow.Server.Instances;

// One participant in an instance. PeerId is unique per instance and
// never reused within the instance's lifetime. When a peer becomes
// inactive (client disconnect or explicit leave with BecomeInactive)
// the slot is retained for PeerTtl so the same UserId can rejoin and
// reclaim it. Master election only counts active peers.
public sealed class Peer(int peerId, string userId)
{
    public int PeerId { get; } = peerId;
    public string UserId { get; } = userId;

    // Per-peer properties keyed by string. All values are PropertyValue
    // (tagged struct - no boxing).
    public Dictionary<string, PropertyValue> Properties { get; } = [];

    // Interest groups this peer receives events for. Group 0 is a
    // permanent broadcast group every peer is implicitly in and cannot
    // leave.
    public HashSet<byte> SubscribedGroups { get; } = [0];

    public bool IsActive { get; set; } = true;
    public int? ConnectionId { get; set; }
    public long? InactiveSinceMs { get; set; }

    // Convenience accessor for the well-known "nickname" property.
    // Chosen freely; server treats it identically to any other key.
    public string? NickName
    {
        get => Properties.TryGetValue("nickname", out var v) && v.Kind == PropertyKind.String
            ? v.AsString
            : null;
        set => Properties["nickname"] = value is null ? PropertyValue.Null : PropertyValue.From(value);
    }
}
