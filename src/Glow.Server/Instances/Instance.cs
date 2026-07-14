using Glow.Shared;
using Glow.Shared.Protocol;

namespace Glow.Server.Instances;

public readonly record struct JoinResult(short ErrorCode, Peer? Peer, bool IsRejoin)
{
    public static JoinResult Ok(Peer peer, bool isRejoin) =>
        new(Glow.Shared.Protocol.ErrorCode.Ok, peer, isRejoin);
    public static JoinResult Fail(short code) => new(code, null, false);
}

// Multiplayer instance. Peer numbering is monotonic within the instance's
// lifetime and never reused. Master election picks the lowest-numbered
// active peer. When zero active peers remain the master id is 0 and the
// caller can destroy the instance after EmptyInstanceTtl.
public sealed class Instance(string name)
{
    public string Name { get; } = name;

    public Dictionary<string, PropertyValue> Properties { get; } = [];

    // PeerId -> Peer. Includes both active and inactive peers.
    public Dictionary<int, Peer> Peers { get; } = [];

    // UserId -> Peer. Populated for every peer regardless of active state
    // so a repeat join with the same UserId can find and rejoin an inactive
    // slot before falling through to fresh allocation.
    public Dictionary<string, Peer> PeersByUserId { get; } = [];

    // NetworkId -> PeerId ownership map for scene objects. Server-side
    // arbitrated: writes are serialized by packet arrival order; CAS
    // gates on the current owner value.
    public Dictionary<int, int> ObjectOwners { get; } = [];

    public MessageCache Cache { get; } = new();

    public int NextPeerId { get; private set; } = 1;
    public int MasterPeerId { get; private set; }

    public int MaxPeers { get; set; }                    // 0 = unlimited
    public bool IsOpen { get; set; } = true;
    public bool CleanupCacheOnLeave { get; set; } = true;
    public bool SuppressJoinLeaveEvents { get; set; }
    public bool BroadcastPropertyChangeToAll { get; set; } = true;
    public int PeerTtlMs { get; set; }
    // Empty-instance destruction TTL. Wired from ServerOptions.EmptyInstanceTtlMs on create.
    //   0 (default) → destroy on the first sweep tick after ActivePeerCount reaches 0 (immediate)
    //   >0          → destroy after that many ms of continuous emptiness
    //   <0          → never destroy (opt out; instance persists across empty periods)
    public int EmptyInstanceTtlMs { get; set; }

    // Server-time (ms) at which this instance became empty. Null while any
    // peer remains; the registry sweeps instances that stay empty past
    // EmptyInstanceTtlMs, releasing the name so a fresh join restarts
    // NextPeerId at 1 -- old peer ids never collide with new arrivals.
    public long? EmptyAtMs { get; private set; }

    public int ActivePeerCount
    {
        get
        {
            var c = 0;
            foreach (var p in Peers.Values) if (p.IsActive) c++;
            return c;
        }
    }

    public IEnumerable<Peer> ActivePeers
    {
        get
        {
            foreach (var p in Peers.Values) if (p.IsActive) yield return p;
        }
    }

    public JoinResult TryJoin(
        string userId,
        Dictionary<string, PropertyValue>? properties,
        JoinMode mode,
        int? connectionId)
    {
        if (PeersByUserId.TryGetValue(userId, out var existing))
        {
            if (existing.IsActive)
                return JoinResult.Fail(Glow.Shared.Protocol.ErrorCode.PeerAlreadyActive);

            existing.IsActive = true;
            existing.ConnectionId = connectionId;
            existing.InactiveSinceMs = null;
            if (properties is not null) MergeInto(existing.Properties, properties);
            ClearEmptyMark();
            RecomputeMaster();
            return JoinResult.Ok(existing, isRejoin: true);
        }

        if (mode == JoinMode.RejoinOnly)
            return JoinResult.Fail(Glow.Shared.Protocol.ErrorCode.PeerRejoinNotFound);
        if (!IsOpen)
            return JoinResult.Fail(Glow.Shared.Protocol.ErrorCode.InstanceClosed);
        if (MaxPeers > 0 && ActivePeerCount >= MaxPeers)
            return JoinResult.Fail(Glow.Shared.Protocol.ErrorCode.InstanceFull);

        var peer = new Peer(NextPeerId++, userId) { ConnectionId = connectionId };
        if (properties is not null) MergeInto(peer.Properties, properties);
        Peers[peer.PeerId] = peer;
        PeersByUserId[userId] = peer;
        ClearEmptyMark();
        RecomputeMaster();
        return JoinResult.Ok(peer, isRejoin: false);
    }

    // Called after any activity that may have dropped active peers to zero.
    // No-op when peers remain or the mark is already set.
    public void MarkEmptyIfNeeded(long nowMs)
    {
        if (EmptyAtMs.HasValue) return;
        if (ActivePeerCount != 0) return;
        EmptyAtMs = nowMs;
    }

    public void ClearEmptyMark() => EmptyAtMs = null;

    public bool MakePeerInactive(int peerId, long nowMs)
    {
        if (!Peers.TryGetValue(peerId, out var peer)) return false;
        if (!peer.IsActive) return false;
        peer.IsActive = false;
        peer.ConnectionId = null;
        peer.InactiveSinceMs = nowMs;
        peer.SubscribedGroups.Clear();
        peer.SubscribedGroups.Add(0);
        RecomputeMaster();
        MarkEmptyIfNeeded(nowMs);
        return true;
    }

    public bool RemovePeer(int peerId, long nowMs)
    {
        if (!Peers.TryGetValue(peerId, out var peer)) return false;
        Peers.Remove(peerId);
        PeersByUserId.Remove(peer.UserId);
        if (CleanupCacheOnLeave) Cache.RemoveForPeer(peerId);
        RecomputeMaster();
        MarkEmptyIfNeeded(nowMs);
        return true;
    }

    // Elects the active peer with the lowest PeerId as master. Returns
    // the new MasterPeerId (0 when no active peer remains).
    public int RecomputeMaster()
    {
        var best = int.MaxValue;
        foreach (var p in Peers.Values)
            if (p.IsActive && p.PeerId < best) best = p.PeerId;
        MasterPeerId = best == int.MaxValue ? 0 : best;
        return MasterPeerId;
    }

    // ---- Object ownership -----------------------------------------

    public (bool Success, int Previous, int Current) TrySetObjectOwner(
        int networkId, int newOwner, bool hasExpected, int expected)
    {
        var previous = ObjectOwners.TryGetValue(networkId, out var p) ? p : 0;
        if (hasExpected && expected != previous)
            return (false, previous, previous);
        if (newOwner == 0)
        {
            ObjectOwners.Remove(networkId);
            return (true, previous, 0);
        }
        ObjectOwners[networkId] = newOwner;
        return (true, previous, newOwner);
    }

    public List<(int NetworkId, int Previous, int Current)> TransferOwnershipFromPeer(
        int leavingPeer, int transferTo)
    {
        var results = new List<(int, int, int)>();
        var owned = new List<int>();
        foreach (var kv in ObjectOwners)
            if (kv.Value == leavingPeer) owned.Add(kv.Key);
        foreach (var nid in owned)
        {
            if (transferTo == 0)
                ObjectOwners.Remove(nid);
            else
                ObjectOwners[nid] = transferTo;
            results.Add((nid, leavingPeer, transferTo));
        }
        return results;
    }

    static void MergeInto(Dictionary<string, PropertyValue> target, Dictionary<string, PropertyValue> source)
    {
        foreach (var kv in source)
        {
            if (kv.Value.IsNull) target.Remove(kv.Key);
            else target[kv.Key] = kv.Value;
        }
    }
}
