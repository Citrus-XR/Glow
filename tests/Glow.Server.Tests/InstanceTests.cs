using Glow.Server.Instances;
using Glow.Shared;
using Glow.Shared.Protocol;
using Xunit;

namespace Glow.Server.Tests;

// Room / Peer / MessageCache / ObjectOwners logic (no networking).
public class InstanceTests
{
    static Instance NewInstance() => new("test");

    [Fact]
    public void FirstJoin_GetsPeerId1()
    {
        var inst = NewInstance();
        var r = inst.TryJoin("alice", null, JoinMode.JoinExisting, 100);
        Assert.Equal(ErrorCode.Ok, r.ErrorCode);
        Assert.Equal(1, r.Peer!.PeerId);
    }

    [Fact]
    public void SequentialJoins_Increment()
    {
        var inst = NewInstance();
        Assert.Equal(1, inst.TryJoin("a", null, JoinMode.JoinExisting, 1).Peer!.PeerId);
        Assert.Equal(2, inst.TryJoin("b", null, JoinMode.JoinExisting, 2).Peer!.PeerId);
        Assert.Equal(3, inst.TryJoin("c", null, JoinMode.JoinExisting, 3).Peer!.PeerId);
    }

    [Fact]
    public void RemovedPeer_DoesNotReusePeerId()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        inst.RemovePeer(1, 0);
        Assert.Equal(2, inst.TryJoin("b", null, JoinMode.JoinExisting, 2).Peer!.PeerId);
    }

    [Fact]
    public void Master_IsLowestActivePeerId()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        inst.TryJoin("b", null, JoinMode.JoinExisting, 2);
        Assert.Equal(1, inst.MasterPeerId);
    }

    [Fact]
    public void Master_MigratesOnRemove()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        inst.TryJoin("b", null, JoinMode.JoinExisting, 2);
        inst.RemovePeer(1, 0);
        Assert.Equal(2, inst.MasterPeerId);
    }

    [Fact]
    public void EmptyInstance_HasZeroMaster()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        inst.RemovePeer(1, 0);
        Assert.Equal(0, inst.MasterPeerId);
    }

    [Fact]
    public void Rejoin_ReclaimsSamePeerId()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        inst.MakePeerInactive(1, 1000);
        var r = inst.TryJoin("a", null, JoinMode.JoinExisting, 55);
        Assert.Equal(ErrorCode.Ok, r.ErrorCode);
        Assert.True(r.IsRejoin);
        Assert.Equal(1, r.Peer!.PeerId);
    }

    [Fact]
    public void DoubleActiveJoin_ReturnsPeerAlreadyActive()
    {
        var inst = NewInstance();
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        var again = inst.TryJoin("a", null, JoinMode.JoinExisting, 2);
        Assert.Equal(ErrorCode.PeerAlreadyActive, again.ErrorCode);
    }

    [Fact]
    public void RejoinOnly_WithoutInactive_Fails()
    {
        var inst = NewInstance();
        var r = inst.TryJoin("a", null, JoinMode.RejoinOnly, 1);
        Assert.Equal(ErrorCode.PeerRejoinNotFound, r.ErrorCode);
    }

    [Fact]
    public void ClosedInstance_ReturnsInstanceClosed()
    {
        var inst = NewInstance();
        inst.IsOpen = false;
        Assert.Equal(ErrorCode.InstanceClosed, inst.TryJoin("a", null, JoinMode.JoinExisting, 1).ErrorCode);
    }

    [Fact]
    public void FullInstance_ReturnsInstanceFull()
    {
        var inst = NewInstance();
        inst.MaxPeers = 1;
        inst.TryJoin("a", null, JoinMode.JoinExisting, 1);
        Assert.Equal(ErrorCode.InstanceFull, inst.TryJoin("b", null, JoinMode.JoinExisting, 2).ErrorCode);
    }

    [Fact]
    public void PropertyMerge_NullValueDeletes()
    {
        var inst = NewInstance();
        var props = new Dictionary<string, PropertyValue>
        {
            ["nickname"] = PropertyValue.From("Alice"),
        };
        inst.TryJoin("a", props, JoinMode.JoinExisting, 1);
        Assert.Equal("Alice", inst.Peers[1].NickName);
    }
}

public class MessageCacheTests
{
    [Fact]
    public void AddedEntries_PreserveOrder()
    {
        var c = new MessageCache();
        c.Add(1, 10, DeliveryMode.ReliableOrdered, 0, new byte[] { 1 });
        c.Add(2, 11, DeliveryMode.ReliableOrdered, 0, new byte[] { 2 });
        c.Add(1, 12, DeliveryMode.ReliableOrdered, 0, new byte[] { 3 });
        Assert.Equal(3, c.Count);
        Assert.Equal(10, c.Entries[0].MessageCode);
        Assert.Equal(11, c.Entries[1].MessageCode);
        Assert.Equal(12, c.Entries[2].MessageCode);
    }

    [Fact]
    public void RemoveForPeer_OnlyClearsSender()
    {
        var c = new MessageCache();
        c.Add(1, 10, DeliveryMode.ReliableOrdered, 0, default);
        c.Add(2, 10, DeliveryMode.ReliableOrdered, 0, default);
        c.Add(1, 11, DeliveryMode.ReliableOrdered, 0, default);
        Assert.Equal(2, c.RemoveForPeer(1));
        Assert.Single(c.Entries);
        Assert.Equal(2, c.Entries[0].SenderPeerId);
    }

    [Fact]
    public void RemoveByCode_WildcardMatches()
    {
        var c = new MessageCache();
        c.Add(1, 10, DeliveryMode.ReliableOrdered, 0, default);
        c.Add(1, 20, DeliveryMode.ReliableOrdered, 0, default);
        c.RemoveByCode(0, 1);
        Assert.Empty(c.Entries);
    }

    [Fact]
    public void CacheEntries_PreserveOriginalDeliveryAndChannel()
    {
        var c = new MessageCache();
        c.Add(1, 10, DeliveryMode.Sequenced, 5, new byte[] { 42 });
        Assert.Equal(DeliveryMode.Sequenced, c.Entries[0].Delivery);
        Assert.Equal((byte)5, c.Entries[0].Channel);
    }

    [Fact]
    public void RemoveByCodeAndKey_OnlyDropsMatchingTriple()
    {
        var c = new MessageCache();
        c.Add(1, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 1 }, cacheKey: 100);
        c.Add(1, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 2 }, cacheKey: 200);
        c.Add(2, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 3 }, cacheKey: 100);
        c.Add(1, 21, DeliveryMode.ReliableOrdered, 0, new byte[] { 4 }, cacheKey: 100);
        Assert.Equal(1, c.RemoveByCodeAndKey(20, 1, 100));
        Assert.Equal(3, c.Count);
        // Remaining entries: (1,20,200), (2,20,100), (1,21,100)
        Assert.All(c.Entries, e => Assert.False(e.SenderPeerId == 1 && e.MessageCode == 20 && e.CacheKey == 100));
    }

    [Fact]
    public void RemoveByCodeAndKeyGlobal_DropsAllSendersForMatchingPair()
    {
        var c = new MessageCache();
        c.Add(1, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 1 }, cacheKey: 100);
        c.Add(2, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 2 }, cacheKey: 100);
        c.Add(3, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 3 }, cacheKey: 100);
        c.Add(1, 20, DeliveryMode.ReliableOrdered, 0, new byte[] { 4 }, cacheKey: 200);
        c.Add(1, 21, DeliveryMode.ReliableOrdered, 0, new byte[] { 5 }, cacheKey: 100);
        // Global variant collapses the (20, 100) slot regardless of sender
        // — three entries share that (code, key) and all must be dropped.
        Assert.Equal(3, c.RemoveByCodeAndKeyGlobal(20, 100));
        Assert.Equal(2, c.Count);
        Assert.All(c.Entries, e => Assert.False(e.MessageCode == 20 && e.CacheKey == 100));
    }

    [Fact]
    public void CachedMessage_DefaultCacheKeyIsZero()
    {
        var c = new MessageCache();
        c.Add(1, 10, DeliveryMode.ReliableOrdered, 0, default);
        Assert.Equal(0, c.Entries[0].CacheKey);
    }
}

public class ObjectOwnerTests
{
    [Fact]
    public void ClaimUnowned_Succeeds()
    {
        var inst = new Instance("r");
        var (ok, prev, cur) = inst.TrySetObjectOwner(42, 1, hasExpected: false, expected: 0);
        Assert.True(ok);
        Assert.Equal(0, prev);
        Assert.Equal(1, cur);
    }

    [Fact]
    public void CAS_Matching_Succeeds()
    {
        var inst = new Instance("r");
        inst.TrySetObjectOwner(42, 1, false, 0);
        var (ok, _, cur) = inst.TrySetObjectOwner(42, 2, hasExpected: true, expected: 1);
        Assert.True(ok);
        Assert.Equal(2, cur);
    }

    [Fact]
    public void CAS_Mismatch_Rejected()
    {
        var inst = new Instance("r");
        inst.TrySetObjectOwner(42, 1, false, 0);
        var (ok, prev, cur) = inst.TrySetObjectOwner(42, 2, hasExpected: true, expected: 99);
        Assert.False(ok);
        Assert.Equal(1, prev);
        Assert.Equal(1, cur);
        Assert.Equal(1, inst.ObjectOwners[42]);
    }

    [Fact]
    public void ReleaseWithZero_ClearsEntry()
    {
        var inst = new Instance("r");
        inst.TrySetObjectOwner(42, 1, false, 0);
        inst.TrySetObjectOwner(42, 0, false, 0);
        Assert.False(inst.ObjectOwners.ContainsKey(42));
    }

    [Fact]
    public void TransferFromPeer_MovesAllOwned()
    {
        var inst = new Instance("r");
        inst.TrySetObjectOwner(1, 1, false, 0);
        inst.TrySetObjectOwner(2, 1, false, 0);
        inst.TrySetObjectOwner(3, 2, false, 0);
        var moves = inst.TransferOwnershipFromPeer(1, 2);
        Assert.Equal(2, moves.Count);
        Assert.Equal(2, inst.ObjectOwners[1]);
        Assert.Equal(2, inst.ObjectOwners[2]);
    }
}

public class InstanceRegistryTests
{
    [Fact]
    public void TryCreate_DuplicateFails()
    {
        var reg = new InstanceRegistry();
        Assert.True(reg.TryCreate("a", out _));
        Assert.False(reg.TryCreate("a", out _));
        Assert.Equal(1, reg.Count);
    }
}
