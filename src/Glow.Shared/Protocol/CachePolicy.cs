using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// SendMessage cache directive. A cached message is replayed to future
// joiners immediately after the JoinInstanceAck and before any live
// messages. Only messages with Routing = Others / All / Group are eligible
// for caching (targeted / master-only messages bypass the cache).
public enum CachePolicy : byte
{
    None = 0,             // do not cache
    AddPerPeer = 1,       // stored in the sender's bucket; cleared when the sender leaves (if CleanupCacheOnLeave)
    AddGlobal = 2,        // stored in the shared instance bucket; sender leaving does not evict it
    RemoveByCode = 3,     // delete cached entries matching (MessageCode, sender)
    RemoveDeparted = 4,   // sweep all buckets belonging to peers no longer active
    ReplaceLatest = 5,    // delete previous entries with matching (sender, MessageCode, CacheKey) then add — one entry per key
}
}
