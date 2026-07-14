using Glow.Shared.Protocol;

namespace Glow.Server.Instances;

// One cached message entry. Server records sender + original delivery/
// channel so replay to late joiners reproduces the exact wire semantics
// the original sender picked. Payload is stored as ReadOnlyMemory so
// replay is zero-copy.
public sealed record CachedMessage(
    int SenderPeerId,
    byte MessageCode,
    DeliveryMode Delivery,
    byte Channel,
    ReadOnlyMemory<byte> Payload,
    int CacheKey = 0);
