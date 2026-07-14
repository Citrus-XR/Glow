using Glow.Server.Persistence;
using Glow.Shared;
using Xunit;

namespace Glow.Server.Tests;

public class PeerDataJsonCodecTests
{
    [Fact]
    public void RoundTripsPrimitives()
    {
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>
        {
            [0] = new()
            {
                ["b"] = PropertyValue.From(true),
                ["i"] = PropertyValue.From(42),
                ["l"] = PropertyValue.From(long.MaxValue),
                ["d"] = PropertyValue.From(3.14),
                ["s"] = PropertyValue.From("hi"),
                ["n"] = PropertyValue.Null,
            },
        };

        var bytes = PeerDataJsonCodec.Encode(stores);
        var d = PeerDataJsonCodec.Decode(bytes);
        Assert.True(d[0]["b"].AsBool);
        // JSON number path widens Int -> Long.
        Assert.Equal(42L, d[0]["i"].AsLong);
        Assert.Equal(long.MaxValue, d[0]["l"].AsLong);
        Assert.Equal(3.14, d[0]["d"].AsDouble);
        Assert.Equal("hi", d[0]["s"].AsString);
        Assert.True(d[0]["n"].IsNull);
    }

    [Fact]
    public void RoundTripsBytes_Base64Wrapper()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>
        {
            [1] = new() { ["b"] = PropertyValue.From(payload) },
        };
        var bytes = PeerDataJsonCodec.Encode(stores);
        Assert.Contains("$b", System.Text.Encoding.UTF8.GetString(bytes));
        var d = PeerDataJsonCodec.Decode(bytes);
        Assert.Equal(payload, d[1]["b"].AsBytes);
    }

    [Fact]
    public void RoundTrips_MultipleTagsIndependently()
    {
        // Client picks any two tags -- they're just byte namespaces.
        // Use 0 and 1 here, but the store treats them identically to
        // any other byte value.
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>
        {
            [0] = new() { ["gold"] = PropertyValue.From(100) },
            [1] = new() { ["chair"] = PropertyValue.From(new byte[] { 9, 9 }) },
        };
        var d = PeerDataJsonCodec.Decode(PeerDataJsonCodec.Encode(stores));
        Assert.Equal(100L, d[0]["gold"].AsLong);
        Assert.Equal(new byte[] { 9, 9 }, d[1]["chair"].AsBytes);
        Assert.DoesNotContain(d[0].Keys, k => k == "chair");
    }

    [Fact]
    public void RoundTrips_ArbitraryNonZeroTag()
    {
        // Nothing special about 0 / 1 -- tag 42 works the same way.
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>
        {
            [42] = new() { ["ok"] = PropertyValue.From("yes") },
        };
        var d = PeerDataJsonCodec.Decode(PeerDataJsonCodec.Encode(stores));
        Assert.Equal("yes", d[42]["ok"].AsString);
    }

    [Fact]
    public void Decode_SkipsNonByteTopLevelKeys()
    {
        // Forward-compat: a hand-added section under a non-numeric key
        // must not wedge the parse.
        var raw = "{ \"0\": {\"k\": 1}, \"comment\": {\"note\": \"ignored\"} }";
        var d = PeerDataJsonCodec.DecodeString(raw);
        Assert.True(d.ContainsKey(0));
        Assert.Equal(1L, d[0]["k"].AsLong);
    }
}

public class PeerDataStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "glow-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Load_Unknown_ReturnsEmpty()
    {
        var s = new PeerDataStore(_dir);
        Assert.Empty(s.Load("nobody", 0));
        Assert.Empty(s.Load("nobody", 1));
        Assert.Empty(s.Load("nobody", 200));
    }

    [Fact]
    public void Merge_PersistsToDisk_AndReloadsFromFreshStore()
    {
        var s = new PeerDataStore(_dir);
        var (ok, _) = s.Merge("alice", 0,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From(1) });
        Assert.True(ok);
        var s2 = new PeerDataStore(_dir);
        Assert.Equal(1L, s2.Load("alice", 0)["k"].AsLong);
    }

    [Fact]
    public void Merge_NullValue_DeletesKey()
    {
        var s = new PeerDataStore(_dir);
        s.Merge("alice", 0, new Dictionary<string, PropertyValue>
        {
            ["a"] = PropertyValue.From(1),
            ["b"] = PropertyValue.From(2),
        });
        s.Merge("alice", 0,
            new Dictionary<string, PropertyValue> { ["a"] = PropertyValue.Null });
        var data = s.Load("alice", 0);
        Assert.False(data.ContainsKey("a"));
        // In-memory cache preserves original kind; disk round-trip would widen to long.
        Assert.Equal(PropertyValue.From(2), data["b"]);
    }

    [Fact]
    public void Merge_TagsAreIndependent()
    {
        var s = new PeerDataStore(_dir);
        s.Merge("alice", 0,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From("t0") });
        s.Merge("alice", 1,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From("t1") });
        s.Merge("alice", 200,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From("t200") });
        Assert.Equal("t0", s.Load("alice", 0)["k"].AsString);
        Assert.Equal("t1", s.Load("alice", 1)["k"].AsString);
        Assert.Equal("t200", s.Load("alice", 200)["k"].AsString);
    }

    [Fact]
    public void Merge_OverQuota_DropsAtomicallyAndReportsFailure()
    {
        // Small quota so the test doesn't have to allocate 100 KB.
        var s = new PeerDataStore(_dir, perStoreQuotaBytes: 1_000);
        s.Merge("alice", 0,
            new Dictionary<string, PropertyValue> { ["seed"] = PropertyValue.From("ok") });

        var big = new byte[2_000];
        var (ok, snap) = s.Merge("alice", 0,
            new Dictionary<string, PropertyValue>
            {
                ["huge"] = PropertyValue.From(big),
                ["also"] = PropertyValue.From("would-also-drop"),
            });
        Assert.False(ok);
        Assert.False(snap.ContainsKey("huge"));
        Assert.False(snap.ContainsKey("also"));
        Assert.Equal("ok", snap["seed"].AsString);
        // Other tags stay untouched.
        Assert.Empty(s.Load("alice", 1));
    }

    [Fact]
    public void Merge_EmptiedTag_IsPrunedFromDisk()
    {
        var s = new PeerDataStore(_dir);
        s.Merge("alice", 5,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From(1) });
        s.Merge("alice", 5,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.Null });
        var fresh = new PeerDataStore(_dir);
        Assert.Empty(fresh.Load("alice", 5));
    }

    [Fact]
    public void EstimateStoreSize_BoundsAreMonotonic()
    {
        var d = new Dictionary<string, PropertyValue>();
        var baseline = PeerDataStore.EstimateStoreSize(d);
        d["k"] = PropertyValue.From("v");
        var after = PeerDataStore.EstimateStoreSize(d);
        Assert.True(after > baseline);
    }

    [Theory]
    [InlineData("alice", "alice.json")]
    [InlineData("user with spaces", "user_with_spaces.json")]
    [InlineData("../etc/passwd", "___etc_passwd.json")]
    public void SanitizesFilename(string userId, string expected)
    {
        var s = new PeerDataStore(_dir);
        s.Merge(userId, 0,
            new Dictionary<string, PropertyValue> { ["k"] = PropertyValue.From(1) });
        Assert.True(File.Exists(Path.Combine(_dir, expected)));
    }
}
