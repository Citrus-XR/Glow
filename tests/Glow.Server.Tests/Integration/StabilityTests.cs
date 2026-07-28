using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using Xunit;

namespace Glow.Server.Tests.Integration;

// End-to-end stability tests over live UDP loopback. Each test spins up
// its own ephemeral server + clients and tears them down.
public class StabilityTests
{
    static async Task<HelloAck> Hello(TestClient c, string userId)
    {
        var tcs = new TaskCompletionSource<HelloAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromIndex = c.ReceivedCount;
        c.Fire(new Hello(Meta.ProtocolVersion, userId, null));
        var ack = await c.WaitFor<HelloAck>(fromIndex: fromIndex);
        return ack;
    }

    static async Task<int> Join(TestClient c, string instance, JoinMode mode = JoinMode.JoinExisting)
    {
        var ack = await JoinAck(c, instance, mode);
        return ack.MyPeerId;
    }

    static async Task<JoinInstanceAck> JoinAck(TestClient c, string instance, JoinMode mode = JoinMode.JoinExisting)
    {
        var reqId = c.AllocateRequestId();
        var resp = await c.Request(reqId, new JoinInstance(reqId, instance, mode,
            new Dictionary<string, PropertyValue>()));
        return Assert.IsType<JoinInstanceAck>(resp);
    }

    // ============================================================
    // 3+ CLIENTS: JOIN + BROADCAST
    // ============================================================

    [Fact]
    public async Task ThreeClients_JoinInSequence_SeeConsistentPeerList()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        await using var c = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        Assert.Equal(1, await Join(a, "test-instance"));
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        Assert.Equal(2, await Join(b, "test-instance"));
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "carol");
        Assert.Equal(3, await Join(c, "test-instance"));

        // A must see two PeerJoined notifications for peers 2 and 3.
        await a.WaitFor<PeerJoined>(p => p.PeerId == 2);
        await a.WaitFor<PeerJoined>(p => p.PeerId == 3);
        // B sees Peer 3's arrival.
        await b.WaitFor<PeerJoined>(p => p.PeerId == 3);
    }

    // ============================================================
    // NEWCOMER: PEER DATA SNAPSHOT (both directions)
    // ============================================================

    [Fact]
    public async Task NewJoiner_ReceivesExistingPeersPeerData()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var setId = a.AllocateRequestId();
        await a.Request(setId, new Shared.Messages.SetPeerData(setId, 0,
            new Dictionary<string, PropertyValue> { ["score"] = PropertyValue.From(100) }));
        var aPeer = await Join(a, "test-instance");

        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bAck = await JoinAck(b, "test-instance");

        Assert.True(bAck.ExistingPeersData.ContainsKey(aPeer));
        var aStores = bAck.ExistingPeersData[aPeer];
        Assert.True(aStores.ContainsKey(0));
        Assert.Equal(PropertyValue.From(100), aStores[0]["score"]);
        Assert.DoesNotContain(bAck.MyPeerId, bAck.ExistingPeersData.Keys);
    }

    [Fact]
    public async Task FirstJoiner_ExistingPeersDataIsEmpty()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var setId = a.AllocateRequestId();
        await a.Request(setId, new Shared.Messages.SetPeerData(setId, 0,
            new Dictionary<string, PropertyValue> { ["score"] = PropertyValue.From(7) }));
        var ack = await JoinAck(a, "test-instance");

        Assert.Empty(ack.ExistingPeersData);
    }

    [Fact]
    public async Task NewJoiner_ExistingPeersData_CoversMultipleStores()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var s0 = a.AllocateRequestId();
        await a.Request(s0, new Shared.Messages.SetPeerData(s0, 0,
            new Dictionary<string, PropertyValue> { ["team"] = PropertyValue.From("red") }));
        var s7 = a.AllocateRequestId();
        await a.Request(s7, new Shared.Messages.SetPeerData(s7, 7,
            new Dictionary<string, PropertyValue> { ["gold"] = PropertyValue.From(42) }));
        var aPeer = await Join(a, "test-instance");

        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bAck = await JoinAck(b, "test-instance");

        var aStores = bAck.ExistingPeersData[aPeer];
        Assert.Equal("red", aStores[0]["team"].AsString);
        Assert.Equal(42, aStores[7]["gold"].AsInt);
    }

    [Fact]
    public async Task NewJoiner_PeerJoinedCarriesTheirOwnPeerData()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        var beforeA = a.ReceivedCount;

        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var setB = b.AllocateRequestId();
        await b.Request(setB, new Shared.Messages.SetPeerData(setB, 0,
            new Dictionary<string, PropertyValue> { ["team"] = PropertyValue.From("red") }));
        var bPeer = await Join(b, "test-instance");

        var join = await a.WaitFor<PeerJoined>(p => p.PeerId == bPeer, timeoutMs: 3000);
        Assert.True(join.PeerData.ContainsKey(0));
        Assert.True(join.PeerData[0].ContainsKey("team"));
        Assert.Equal("red", join.PeerData[0]["team"].AsString);
    }

    // ============================================================
    // CACHE REPLAY
    // ============================================================

    [Fact]
    public async Task CachedMessages_ReplayedToLateJoiner_InOrder()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 10, Routing.Others, null, 0,
            CachePolicy.AddPerPeer, DeliveryMode.ReliableOrdered, (byte)0, System.Text.Encoding.UTF8.GetBytes("first")));
        a.Fire(new Shared.Messages.SendMessage(0, 11, Routing.Others, null, 0,
            CachePolicy.AddPerPeer, DeliveryMode.ReliableOrdered, (byte)0, System.Text.Encoding.UTF8.GetBytes("second")));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var first = await b.WaitFor<IncomingCachedMessage>(m => m.MessageCode == 10);
        var second = await b.WaitFor<IncomingCachedMessage>(m => m.MessageCode == 11);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(first.Payload.Span));
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(second.Payload.Span));
    }

    [Fact]
    public async Task ReplaceLatest_SameKey_KeepsOnlyLastPayload()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        for (var i = 1; i <= 3; i++)
        {
            a.Fire(new Shared.Messages.SendMessage(0, 30, Routing.Others, null, 0,
                CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
                System.Text.Encoding.UTF8.GetBytes("snap-" + i), CacheKey: 777));
        }
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        Assert.Single(cache.Entries);
        Assert.Equal("snap-3", System.Text.Encoding.UTF8.GetString(cache.Entries[0].Payload.Span));
        Assert.Equal(777, cache.Entries[0].CacheKey);

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");
        var replayed = await b.WaitFor<IncomingCachedMessage>(m => m.MessageCode == 30);
        Assert.Equal("snap-3", System.Text.Encoding.UTF8.GetString(replayed.Payload.Span));
    }

    [Fact]
    public async Task ReplaceLatest_DifferentKeys_AllRetained()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 40, Routing.Others, null, 0,
            CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("obj-A"), CacheKey: 1));
        a.Fire(new Shared.Messages.SendMessage(0, 40, Routing.Others, null, 0,
            CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("obj-B"), CacheKey: 2));
        a.Fire(new Shared.Messages.SendMessage(0, 40, Routing.Others, null, 0,
            CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("obj-C"), CacheKey: 3));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        Assert.Equal(3, cache.Entries.Count);
        var keys = cache.Entries.Select(e => e.CacheKey).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, keys);
    }

    // Regression coverage for the per-sender ReplaceLatest semantics: two
    // different senders writing under the same (code, key) each retain
    // their own snapshot — the policy scopes uniqueness per sender.
    [Fact]
    public async Task ReplaceLatest_DifferentSenders_KeepsPerSenderEntries()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 50, Routing.Others, null, 0,
            CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("snap-alice"), CacheKey: 555));
        b.Fire(new Shared.Messages.SendMessage(0, 50, Routing.Others, null, 0,
            CachePolicy.ReplaceLatest, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("snap-bob"), CacheKey: 555));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        var forKey = cache.Entries.Where(e => e.MessageCode == 50 && e.CacheKey == 555).ToArray();
        Assert.Equal(2, forKey.Length);
        var payloads = forKey.Select(e => System.Text.Encoding.UTF8.GetString(e.Payload.Span))
            .OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "snap-alice", "snap-bob" }, payloads);
    }

    // ReplaceLatestGlobal collapses the (code, key) slot across all
    // senders. Alice's snapshot is superseded by Bob's write; a late
    // joiner sees only the winning entry.
    //
    // NOTE: the policy is defined as "server-arrival last-wins". Firing
    // two SendMessages from two sockets in quick succession only orders
    // them at the sender; UDP arrival order at the server is not
    // guaranteed. To make the test deterministic we wait for the peer
    // opposite to observe each write (Routing.Others broadcast → mirrors
    // the server-side handler ordering faithfully) before firing the
    // next one.
    [Fact]
    public async Task ReplaceLatestGlobal_SameKey_DifferentSenders_KeepsOnlyLast()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 60, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("snap-alice"), CacheKey: 777));
        // Barrier: block until Bob's socket has actually observed Alice's
        // broadcast. Once b sees it, the server has already processed Alice's
        // Add — Bob's subsequent Fire is guaranteed to hit RemoveByCodeAndKey
        // on a cache that contains "snap-alice".
        await b.WaitFor<IncomingMessage>(m => m.MessageCode == 60);

        b.Fire(new Shared.Messages.SendMessage(0, 60, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("snap-bob"), CacheKey: 777));
        // Symmetric barrier so we know Bob's message has been processed
        // before we inspect the cache. Alice sees Bob because Routing.Others
        // excludes the sender.
        await a.WaitFor<IncomingMessage>(m => m.MessageCode == 60);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        var forKey = cache.Entries.Where(e => e.MessageCode == 60 && e.CacheKey == 777).ToArray();
        Assert.Single(forKey);
        Assert.Equal("snap-bob", System.Text.Encoding.UTF8.GetString(forKey[0].Payload.Span));

        await using var c = new TestClient();
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "charlie");
        await Join(c, "test-instance");

        var replayed = await c.WaitFor<IncomingCachedMessage>(m => m.MessageCode == 60);
        Assert.Equal("snap-bob", System.Text.Encoding.UTF8.GetString(replayed.Payload.Span));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var replays = c.Received.OfType<IncomingCachedMessage>().Where(m => m.MessageCode == 60).ToArray();
        Assert.Single(replays);
    }

    // Distinct CacheKey values name distinct logical slots even under the
    // global policy, so writes to different keys never displace each other.
    [Fact]
    public async Task ReplaceLatestGlobal_DifferentKeys_AllRetained()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 65, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("obj-A"), CacheKey: 1));
        a.Fire(new Shared.Messages.SendMessage(0, 65, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("obj-B"), CacheKey: 2));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        var forCode = cache.Entries.Where(e => e.MessageCode == 65).ToArray();
        Assert.Equal(2, forCode.Length);
        var keys = forCode.Select(e => e.CacheKey).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { 1, 2 }, keys);

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        await b.WaitFor<IncomingCachedMessage>(m =>
            m.MessageCode == 65 && System.Text.Encoding.UTF8.GetString(m.Payload.Span) == "obj-A");
        await b.WaitFor<IncomingCachedMessage>(m =>
            m.MessageCode == 65 && System.Text.Encoding.UTF8.GetString(m.Payload.Span) == "obj-B");
    }

    // (code, key) is compound: the same CacheKey under a different
    // MessageCode is a separate logical slot and must not be evicted.
    [Fact]
    public async Task ReplaceLatestGlobal_DifferentCode_NoInterference()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 70, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("code-70"), CacheKey: 42));
        a.Fire(new Shared.Messages.SendMessage(0, 71, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("code-71"), CacheKey: 42));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        var cache = srv.Server.Instances.All["test-instance"].Cache;
        var forKey = cache.Entries.Where(e => e.CacheKey == 42).ToArray();
        Assert.Equal(2, forKey.Length);
        var codes = forKey.Select(e => e.MessageCode).OrderBy(c => c).ToArray();
        Assert.Equal(new byte[] { 70, 71 }, codes);
    }

    // ============================================================
    // MASTER ROTATION
    // ============================================================

    [Fact]
    public async Task Master_AbruptDisconnect_MigratesCleanly()
    {
        await using var srv = new ServerHarness();
        var a = new TestClient();
        await using var b = new TestClient();
        await using var c = new TestClient();

        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bPeer = await Join(b, "test-instance");
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "carol");
        await Join(c, "test-instance");

        Assert.Equal(aPeer, srv.Server.Instances.All["test-instance"].MasterPeerId);

        a.Disconnect();
        await a.DisposeAsync();

        var leaveA = await c.WaitFor<PeerLeft>(p => p.PeerId == aPeer);
        Assert.Equal(bPeer, leaveA.NewMasterPeerId);
        Assert.Equal(bPeer, srv.Server.Instances.All["test-instance"].MasterPeerId);
    }

    [Fact]
    public async Task Master_FiveActorChurn_ConvergesToLastRemaining()
    {
        await using var srv = new ServerHarness();
        var clients = new TestClient[5];
        var peerIds = new int[5];
        for (var i = 0; i < 5; i++)
        {
            clients[i] = new TestClient();
            Assert.True(await clients[i].ConnectAsync(srv.Port));
            await Hello(clients[i], $"u-{i}");
            peerIds[i] = await Join(clients[i], "test-instance");
        }
        Assert.Equal(peerIds[0], srv.Server.Instances.All["test-instance"].MasterPeerId);

        for (var i = 0; i < 4; i++)
        {
            var reqId = clients[i].AllocateRequestId();
            await clients[i].Request(reqId, new LeaveInstance(reqId, false));
            await clients[i].DisposeAsync();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(peerIds[4], srv.Server.Instances.All["test-instance"].MasterPeerId);
        await clients[4].DisposeAsync();
    }

    // ============================================================
    // SEND MESSAGE: ROUTING
    // ============================================================

    [Fact]
    public async Task SendMessage_Others_ExcludesSender()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var beforeA = a.ReceivedCount;
        a.Fire(new Shared.Messages.SendMessage(0, 44, Routing.Others, null, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("for-others")));
        await b.WaitFor<IncomingMessage>(m => m.MessageCode == 44);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(a.Received.Skip(beforeA), m => m is IncomingMessage im && im.MessageCode == 44);
    }

    [Fact]
    public async Task SendMessage_All_IncludesSender()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 55, Routing.All, null, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("bcast")));
        var atA = await a.WaitFor<IncomingMessage>(m => m.MessageCode == 55);
        var atB = await b.WaitFor<IncomingMessage>(m => m.MessageCode == 55);
        Assert.Equal(aPeer, atA.SenderPeerId);
        Assert.Equal(aPeer, atB.SenderPeerId);
    }

    [Fact]
    public async Task SendMessage_Master_OnlyMasterReceives()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        await using var c = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");   // master
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "carol");
        await Join(c, "test-instance");

        c.Fire(new Shared.Messages.SendMessage(0, 66, Routing.Master, null, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("to-master")));
        await a.WaitFor<IncomingMessage>(m => m.MessageCode == 66);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(b.Received, m => m is IncomingMessage im && im.MessageCode == 66);
        Assert.DoesNotContain(c.Received, m => m is IncomingMessage im && im.MessageCode == 66);
    }

    [Fact]
    public async Task SendMessage_Peers_OnlyTargetsReceive()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        await using var c = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bPeer = await Join(b, "test-instance");
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "carol");
        await Join(c, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 77, Routing.Peers, new[] { bPeer }, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("for-b")));
        await b.WaitFor<IncomingMessage>(m => m.MessageCode == 77);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(c.Received, m => m is IncomingMessage im && im.MessageCode == 77);
    }

    // ============================================================
    // RPC ORDERING
    // ============================================================

    [Fact]
    public async Task SendMessage_100Rapid_PayloadOrderPreserved()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");
        var beforeB = b.ReceivedCount;

        for (var i = 0; i < 100; i++)
        {
            a.Fire(new Shared.Messages.SendMessage(0, 99, Routing.Others, null, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
                BitConverter.GetBytes(i)));
        }
        await b.WaitFor<IncomingMessage>(m =>
            m.MessageCode == 99 && BitConverter.ToInt32(m.Payload.Span) == 99, timeoutMs: 5000);

        var payloads = b.Received.Skip(beforeB)
            .OfType<IncomingMessage>()
            .Where(m => m.MessageCode == 99)
            .Select(m => BitConverter.ToInt32(m.Payload.Span))
            .ToArray();
        Assert.Equal(Enumerable.Range(0, 100).ToArray(), payloads);
    }

    // ============================================================
    // OBJECT OWNERSHIP
    // ============================================================

    [Fact]
    public async Task ObjectOwner_Claim_BroadcastsToRoom()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var reqId = a.AllocateRequestId();
        var resp = await a.Request(reqId,
            new Shared.Messages.SetObjectOwner(reqId, 42, aPeer, HasExpected: false, Expected: 0));
        var ack = Assert.IsType<SetObjectOwnerAck>(resp);
        Assert.Equal(aPeer, ack.Current);
        Assert.Equal(0, ack.Previous);
        var evt = await b.WaitFor<ObjectOwnerChanged>(e => e.NetworkId == 42);
        Assert.Equal(aPeer, evt.Current);
    }

    [Fact]
    public async Task ObjectOwner_CAS_MismatchReturnsError()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bPeer = await Join(b, "test-instance");

        var reqA = a.AllocateRequestId();
        await a.Request(reqA,
            new Shared.Messages.SetObjectOwner(reqA, 9, aPeer, false, 0));

        var reqB = b.AllocateRequestId();
        var resp = await b.Request(reqB,
            new Shared.Messages.SetObjectOwner(reqB, 9, bPeer, HasExpected: true, Expected: 0));
        var err = Assert.IsType<Error>(resp);
        Assert.Equal(ErrorCode.CasMismatch, err.Code);
    }

    [Fact]
    public async Task StateMessage_ExplicitNonOwner_IsRejectedBeforeBroadcast()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var requestId = a.AllocateRequestId();
        await a.Request(requestId,
            new Shared.Messages.SetObjectOwner(requestId, 42, aPeer, false, 0));

        var payload = new PayloadWriter().PutInt(42).ToPayload();
        var before = a.ReceivedCount;
        b.Fire(new Shared.Messages.SendMessage(0, 21, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, 2, payload, 42));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(a.Received.Skip(before).OfType<IncomingMessage>(),
            message => message.MessageCode == 21);

        a.Fire(new Shared.Messages.SendMessage(0, 21, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, 2, payload, 42));
        await b.WaitFor<IncomingMessage>(message => message.MessageCode == 21);
    }

    [Fact]
    public async Task StateMessage_PlayerObjectId_RequiresBoundPlayer()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var networkId = aPeer * 100_000 + 110;
        var payload = new PayloadWriter().PutInt(networkId).ToPayload();
        var before = a.ReceivedCount;
        b.Fire(new Shared.Messages.SendMessage(0, 20, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, 2,
            payload, networkId << 8));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(a.Received.Skip(before).OfType<IncomingMessage>(),
            message => message.MessageCode == 20);

        a.Fire(new Shared.Messages.SendMessage(0, 20, Routing.Others, null, 0,
            CachePolicy.ReplaceLatestGlobal, DeliveryMode.ReliableOrdered, 2,
            payload, networkId << 8));
        await b.WaitFor<IncomingMessage>(message => message.MessageCode == 20);
    }

    [Fact]
    public async Task ObjectOwner_OwnerLeaves_TransfersToMaster()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        await using var c = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var bPeer = await Join(b, "test-instance");
        Assert.True(await c.ConnectAsync(srv.Port));
        await Hello(c, "carol");
        await Join(c, "test-instance");

        var reqB = b.AllocateRequestId();
        await b.Request(reqB, new Shared.Messages.SetObjectOwner(reqB, 10, bPeer, false, 0));
        var reqB2 = b.AllocateRequestId();
        await b.Request(reqB2, new Shared.Messages.SetObjectOwner(reqB2, 11, bPeer, false, 0));

        var leaveReq = b.AllocateRequestId();
        await b.Request(leaveReq, new LeaveInstance(leaveReq, false));

        var move10 = await a.WaitFor<ObjectOwnerChanged>(e =>
            e.NetworkId == 10 && e.Current == aPeer);
        var move11 = await a.WaitFor<ObjectOwnerChanged>(e =>
            e.NetworkId == 11 && e.Current == aPeer);
        Assert.Equal(bPeer, move10.Previous);
        Assert.Equal(bPeer, move11.Previous);
    }

    [Fact]
    public async Task ObjectOwner_JoinInstanceAck_HasSnapshot()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");

        var reqA = a.AllocateRequestId();
        await a.Request(reqA, new Shared.Messages.SetObjectOwner(reqA, 5, aPeer, false, 0));
        var reqA2 = a.AllocateRequestId();
        await a.Request(reqA2, new Shared.Messages.SetObjectOwner(reqA2, 6, aPeer, false, 0));

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var reqB = b.AllocateRequestId();
        var resp = await b.Request(reqB, new JoinInstance(reqB, "test-instance", JoinMode.JoinExisting,
            new Dictionary<string, PropertyValue>()));
        var ack = Assert.IsType<JoinInstanceAck>(resp);
        Assert.Equal(aPeer, ack.ObjectOwners[5]);
        Assert.Equal(aPeer, ack.ObjectOwners[6]);
    }

    // ============================================================
    // PROPERTIES + CAS
    // ============================================================

    [Fact]
    public async Task SetProperty_CAS_MismatchReturnsError()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        var reqA = a.AllocateRequestId();
        await a.Request(reqA, new SetProperty(reqA, 0, "mode",
            PropertyValue.From("warmup"), HasExpected: false, Expected: PropertyValue.Null));

        var reqA2 = a.AllocateRequestId();
        var resp = await a.Request(reqA2, new SetProperty(reqA2, 0, "mode",
            PropertyValue.From("match"), HasExpected: true, Expected: PropertyValue.From("stale")));
        var err = Assert.IsType<Error>(resp);
        Assert.Equal(ErrorCode.CasMismatch, err.Code);
    }

    // ============================================================
    // INTEREST GROUPS
    // ============================================================

    [Fact]
    public async Task InterestGroup_FilterAppliesToSendMessage()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        await using var sender = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");
        Assert.True(await sender.ConnectAsync(srv.Port));
        await Hello(sender, "sender");
        await Join(sender, "test-instance");

        a.Fire(new SubscribeGroups(new byte[] { 5 }, Array.Empty<byte>()));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        sender.Fire(new Shared.Messages.SendMessage(0, 77, Routing.Group, null, 5, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("group5")));
        await a.WaitFor<IncomingMessage>(m => m.MessageCode == 77);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(b.Received, m => m is IncomingMessage im && im.MessageCode == 77);
    }

    // ============================================================
    // PEER DATA PERSISTENCE
    // ============================================================

    [Fact]
    public async Task PeerData_SurvivesReconnect()
    {
        await using var srv = new ServerHarness();
        {
            await using var a = new TestClient();
            Assert.True(await a.ConnectAsync(srv.Port));
            await Hello(a, "alice");
            var reqSet = a.AllocateRequestId();
            await a.Request(reqSet, new Shared.Messages.SetPeerData(reqSet, 0,
                new Dictionary<string, PropertyValue>
                {
                    ["gold"] = PropertyValue.From(999),
                    ["motto"] = PropertyValue.From("carpe diem"),
                }));
            a.Disconnect();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        {
            await using var a2 = new TestClient();
            Assert.True(await a2.ConnectAsync(srv.Port));
            var ack = await Hello(a2, "alice");
            // The store's in-memory cache preserves original kinds
            // (Int stays Int); disk-only readers would see Long.
            Assert.Equal(PropertyValue.From(999), ack.PeerData[0]["gold"]);
            Assert.Equal("carpe diem", ack.PeerData[0]["motto"].AsString);
        }
    }

    // ============================================================
    // DISCONNECT SCENARIOS
    // ============================================================

    [Fact]
    public async Task Disconnect_MidJoin_ServerCleansUp()
    {
        await using var srv = new ServerHarness();
        var quick = new TestClient();
        Assert.True(await quick.ConnectAsync(srv.Port));
        quick.Fire(new Hello(Meta.ProtocolVersion, "flakey", null));
        quick.Fire(new JoinInstance(1, "test-instance", JoinMode.JoinExisting, []));
        quick.Disconnect();
        await quick.DisposeAsync();
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(srv.Server.Transport.Sessions,
            s => s.UserId == "flakey" && s.IsInInstance);

        // Server still healthy for fresh clients.
        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        Assert.True(await Join(b, "test-instance") >= 1);
    }

    [Fact]
    public async Task Disconnect_AfterHello_BeforeJoin_NoLeak()
    {
        await using var srv = new ServerHarness();
        var quick = new TestClient();
        Assert.True(await quick.ConnectAsync(srv.Port));
        await Hello(quick, "transient");
        quick.Disconnect();
        await quick.DisposeAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var inst = srv.Server.Instances.All["test-instance"];
        Assert.Empty(inst.Peers);
        Assert.Equal(0, inst.MasterPeerId);
    }

    // ============================================================
    // STATE-MACHINE VIOLATIONS
    // ============================================================

    [Fact]
    public async Task Join_BeforeHello_ReturnsNotAuthenticated()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        var reqId = a.AllocateRequestId();
        var resp = await a.Request(reqId, new JoinInstance(reqId, "test-instance",
            JoinMode.JoinExisting, []));
        var err = Assert.IsType<Error>(resp);
        Assert.Equal(ErrorCode.NotAuthenticated, err.Code);
    }

    [Fact]
    public async Task Join_Twice_ReturnsAlreadyInInstance()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        var reqId = a.AllocateRequestId();
        var resp = await a.Request(reqId, new JoinInstance(reqId, "test-instance",
            JoinMode.JoinExisting, []));
        var err = Assert.IsType<Error>(resp);
        Assert.Equal(ErrorCode.AlreadyInInstance, err.Code);
    }

    // ============================================================
    // MULTI-INSTANCE ISOLATION
    // ============================================================

    [Fact]
    public async Task Instances_AreIsolatedFromEachOther()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var reqA = a.AllocateRequestId();
        await a.Request(reqA, new JoinInstance(reqA, "room-A", JoinMode.JoinOrCreate, []));

        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var reqB = b.AllocateRequestId();
        await b.Request(reqB, new JoinInstance(reqB, "room-B", JoinMode.JoinOrCreate, []));

        a.Fire(new Shared.Messages.SendMessage(0, 33, Routing.All, null, 0, CachePolicy.None, DeliveryMode.ReliableOrdered, (byte)0,
            System.Text.Encoding.UTF8.GetBytes("room-A-only")));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(b.Received, m => m is IncomingMessage im && im.MessageCode == 33);
    }

    // ============================================================
    // DELIVERY MODE + CHANNEL
    // ============================================================

    // Sender picks Delivery + Channel; server echoes both back on the
    // IncomingMessage so the receiver can dispatch per-channel.
    [Fact]
    public async Task SendMessage_DeliveryAndChannel_EchoedOnReceive()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 77, Routing.Others, null, 0, CachePolicy.None,
            DeliveryMode.Sequenced, (byte)5,
            System.Text.Encoding.UTF8.GetBytes("seq5")));
        var im = await b.WaitFor<IncomingMessage>(m => m.MessageCode == 77);
        Assert.Equal(DeliveryMode.Sequenced, im.Delivery);
        Assert.Equal((byte)5, im.Channel);
    }

    // Two ordered streams on different channels don't block each other:
    // even under bursty traffic each channel maintains its own order.
    [Fact]
    public async Task SendMessage_DifferentChannels_IndependentOrder()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        await using var b = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        for (var i = 0; i < 50; i++)
        {
            a.Fire(new Shared.Messages.SendMessage(0, 88, Routing.Others, null, 0, CachePolicy.None,
                DeliveryMode.ReliableOrdered, (byte)1, BitConverter.GetBytes(i)));
            a.Fire(new Shared.Messages.SendMessage(0, 89, Routing.Others, null, 0, CachePolicy.None,
                DeliveryMode.ReliableOrdered, (byte)2, BitConverter.GetBytes(i)));
        }

        await b.WaitFor<IncomingMessage>(m =>
            m.MessageCode == 88 && BitConverter.ToInt32(m.Payload.Span) == 49);
        await b.WaitFor<IncomingMessage>(m =>
            m.MessageCode == 89 && BitConverter.ToInt32(m.Payload.Span) == 49);

        var ch1 = b.Received.OfType<IncomingMessage>().Where(m => m.Channel == 1 && m.MessageCode == 88)
            .Select(m => BitConverter.ToInt32(m.Payload.Span)).ToArray();
        var ch2 = b.Received.OfType<IncomingMessage>().Where(m => m.Channel == 2 && m.MessageCode == 89)
            .Select(m => BitConverter.ToInt32(m.Payload.Span)).ToArray();
        Assert.Equal(Enumerable.Range(0, 50).ToArray(), ch1);
        Assert.Equal(Enumerable.Range(0, 50).ToArray(), ch2);
    }

    [Fact]
    public async Task SendMessage_ChannelOutOfRange_ReturnsError()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        var reqId = a.AllocateRequestId();
        var resp = await a.Request(reqId, new Shared.Messages.SendMessage(
            reqId, 44, Routing.Others, null, 0, CachePolicy.None,
            DeliveryMode.ReliableOrdered, (byte)99, Array.Empty<byte>()));
        var err = Assert.IsType<Error>(resp);
        Assert.Equal(ErrorCode.InvalidMessage, err.Code);
    }

    [Fact]
    public async Task CachedMessage_ReplayPreservesDeliveryAndChannel()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        a.Fire(new Shared.Messages.SendMessage(0, 55, Routing.Others, null, 0,
            CachePolicy.AddPerPeer, DeliveryMode.Sequenced, (byte)7,
            System.Text.Encoding.UTF8.GetBytes("cached")));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        await Join(b, "test-instance");

        var im = await b.WaitFor<IncomingCachedMessage>(m => m.MessageCode == 55);
        Assert.Equal(DeliveryMode.Sequenced, im.Delivery);
        Assert.Equal((byte)7, im.Channel);
    }

    // ============================================================
    // JOIN-TIME OBJECT CLAIM
    // ============================================================

    // Master enters an empty instance carrying a preset id list; the
    // JoinInstanceAck already reflects ownership of every id it claimed.
    [Fact]
    public async Task JoinInstance_WithClaimObjectIds_AckReflectsOwnership()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");

        var reqId = a.AllocateRequestId();
        var msg = new JoinInstance(reqId, "test-instance", JoinMode.JoinExisting,
            new Dictionary<string, PropertyValue>())
        {
            ClaimObjectIds = new[] { 1, 2, 3, 4, 5 },
        };
        var resp = await a.Request(reqId, msg);
        var ack = Assert.IsType<JoinInstanceAck>(resp);
        Assert.Equal(5, ack.ObjectOwners.Count);
        foreach (var nid in new[] { 1, 2, 3, 4, 5 })
            Assert.Equal(ack.MyPeerId, ack.ObjectOwners[nid]);
    }

    // A newcomer arriving with --claim generates one ObjectOwnerChanged
    // per successful claim to peers already in the instance.
    [Fact]
    public async Task JoinInstance_WithClaimObjectIds_BroadcastsToOthers()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        await Join(a, "test-instance");

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var reqId = b.AllocateRequestId();
        var msg = new JoinInstance(reqId, "test-instance", JoinMode.JoinExisting,
            new Dictionary<string, PropertyValue>())
        {
            ClaimObjectIds = new[] { 10, 11 },
        };
        var resp = await b.Request(reqId, msg);
        var ack = Assert.IsType<JoinInstanceAck>(resp);
        var bPeer = ack.MyPeerId;

        var e10 = await a.WaitFor<ObjectOwnerChanged>(e => e.NetworkId == 10);
        var e11 = await a.WaitFor<ObjectOwnerChanged>(e => e.NetworkId == 11);
        Assert.Equal(bPeer, e10.Current);
        Assert.Equal(bPeer, e11.Current);
        Assert.Equal(0, e10.Previous);
        Assert.Equal(0, e11.Previous);
    }

    // If an id is already owned, the claim silently skips it - no CAS
    // error to the joiner, no ObjectOwnerChanged broadcast, and the
    // ack's ObjectOwners still reports the existing owner.
    [Fact]
    public async Task JoinInstance_ClaimAlreadyOwned_IsSkippedSilently()
    {
        await using var srv = new ServerHarness();
        await using var a = new TestClient();
        Assert.True(await a.ConnectAsync(srv.Port));
        await Hello(a, "alice");
        var aPeer = await Join(a, "test-instance");
        var reqA = a.AllocateRequestId();
        await a.Request(reqA, new Shared.Messages.SetObjectOwner(reqA, 42, aPeer, false, 0));
        var beforeA = a.ReceivedCount;

        await using var b = new TestClient();
        Assert.True(await b.ConnectAsync(srv.Port));
        await Hello(b, "bob");
        var reqB = b.AllocateRequestId();
        var msg = new JoinInstance(reqB, "test-instance", JoinMode.JoinExisting,
            new Dictionary<string, PropertyValue>())
        {
            ClaimObjectIds = new[] { 42, 43 },   // 42 is already owned by A; 43 is free
        };
        var resp = await b.Request(reqB, msg);
        var ack = Assert.IsType<JoinInstanceAck>(resp);
        var bPeer = ack.MyPeerId;

        Assert.Equal(aPeer, ack.ObjectOwners[42]);   // still A's
        Assert.Equal(bPeer, ack.ObjectOwners[43]);   // B got the free one

        // A sees the 43 broadcast but NOT any change for 42.
        var e43 = await a.WaitFor<ObjectOwnerChanged>(e => e.NetworkId == 43);
        Assert.Equal(bPeer, e43.Current);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(a.Received.Skip(beforeA),
            m => m is ObjectOwnerChanged e && e.NetworkId == 42);
    }
}
