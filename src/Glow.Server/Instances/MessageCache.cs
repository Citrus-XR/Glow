using Glow.Shared.Protocol;

namespace Glow.Server.Instances;

// Instance-scoped message cache. Callers gate on cache policy before
// adding; entries carry the original sender's delivery + channel so
// replay to late joiners preserves the wire semantics.
public sealed class MessageCache
{
    readonly List<CachedMessage> _entries = [];

    public IReadOnlyList<CachedMessage> Entries => _entries;
    public int Count => _entries.Count;

    public void Add(int senderPeerId, byte messageCode, DeliveryMode delivery, byte channel, ReadOnlyMemory<byte> payload, int cacheKey = 0) =>
        _entries.Add(new CachedMessage(senderPeerId, messageCode, delivery, channel, payload, cacheKey));

    public int RemoveForPeer(int peerId) =>
        _entries.RemoveAll(e => e.SenderPeerId == peerId);

    public int RemoveByCode(byte messageCode, int senderPeerId) =>
        _entries.RemoveAll(e =>
            (messageCode == 0 || e.MessageCode == messageCode) &&
            (senderPeerId == 0 || e.SenderPeerId == senderPeerId));

    // Drop any prior entry with the same (sender, code, key) so ReplaceLatest
    // keeps a single most-recent snapshot per logical stream.
    public int RemoveByCodeAndKey(byte messageCode, int senderPeerId, int cacheKey) =>
        _entries.RemoveAll(e =>
            e.MessageCode == messageCode &&
            e.SenderPeerId == senderPeerId &&
            e.CacheKey == cacheKey);

    public void Clear() => _entries.Clear();
}
