namespace Glow.Shared.Protocol
{
// SendMessage cache directive. A cached message is replayed to future
// joiners immediately after the JoinInstanceAck and before any live
// messages. Only messages with Routing = Others / All / Group are eligible
// for caching (targeted / master-only messages bypass the cache).
//
// Slot-scoped policies (ReplaceLatest, ReplaceLatestGlobal) collapse the
// cache to a single most-recent entry per logical key. They differ only in
// the uniqueness scope:
//
//   ReplaceLatest       — unique per (SenderPeerId, MessageCode, CacheKey).
//                         Each peer owns an independent stream; three
//                         peers writing under the same key retain three
//                         entries. Fits per-peer state broadcasts.
//
//   ReplaceLatestGlobal — unique per (MessageCode, CacheKey), sender-
//                         agnostic. The key names a shared logical slot;
//                         when ownership of that slot moves between peers,
//                         the older peer's snapshot is superseded rather
//                         than replayed alongside the new one. Fits
//                         payloads that represent "the current state of
//                         shared object X" where only the latest write
//                         matters, regardless of author.
public enum CachePolicy : byte
{
    None = 0,             // do not cache
    AddPerPeer = 1,       // stored in the sender's bucket; cleared when the sender leaves (if CleanupCacheOnLeave)
    AddGlobal = 2,        // stored in the shared instance bucket; sender leaving does not evict it
    RemoveByCode = 3,     // delete cached entries matching (MessageCode, sender)
    RemoveDeparted = 4,   // sweep all buckets belonging to peers no longer active
    ReplaceLatest = 5,    // delete previous entries with matching (sender, MessageCode, CacheKey) then add — one entry per (sender, key)
    ReplaceLatestGlobal = 6, // delete previous entries with matching (MessageCode, CacheKey) across all senders then add — one entry per shared key
}
}
