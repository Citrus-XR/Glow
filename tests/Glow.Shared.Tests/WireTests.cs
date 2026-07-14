using System.Text;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using Glow.Shared.Wire;
using LiteNetLib.Utils;
using Xunit;

namespace Glow.Shared.Tests;

// PropertyValue behaviour: factories, accessors, equality (with proper
// bytewise / ordinal string comparison so CAS works over the wire).
public class PropertyValueTests
{
    [Fact]
    public void Null_HasNullKind() => Assert.True(PropertyValue.Null.IsNull);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bool_RoundTrips(bool v)
    {
        var p = PropertyValue.From(v);
        Assert.Equal(PropertyKind.Bool, p.Kind);
        Assert.Equal(v, p.AsBool);
    }

    [Fact]
    public void Int_RoundTrips() => Assert.Equal(42, PropertyValue.From(42).AsInt);

    [Fact]
    public void Long_RoundTrips() => Assert.Equal(long.MaxValue, PropertyValue.From(long.MaxValue).AsLong);

    [Fact]
    public void Float_RoundTrips() => Assert.Equal(3.14f, PropertyValue.From(3.14f).AsFloat);

    [Fact]
    public void Double_RoundTrips() => Assert.Equal(2.71828, PropertyValue.From(2.71828).AsDouble);

    [Fact]
    public void String_RoundTrips() => Assert.Equal("hi", PropertyValue.From("hi").AsString);

    [Fact]
    public void Bytes_RoundTrips()
    {
        var b = new byte[] { 1, 2, 3, 4 };
        Assert.Equal(b, PropertyValue.From(b).AsBytes);
    }

    [Fact]
    public void Equality_DifferentKind_NotEqual() =>
        Assert.NotEqual(PropertyValue.From(42), PropertyValue.From((long)42));

    [Fact]
    public void Equality_SameString_Equal() =>
        Assert.Equal(PropertyValue.From("a"), PropertyValue.From("a"));

    [Fact]
    public void Equality_Bytes_StructuralCompare()
    {
        var a = PropertyValue.From(new byte[] { 1, 2, 3 });
        var b = PropertyValue.From(new byte[] { 1, 2, 3 });
        var c = PropertyValue.From(new byte[] { 1, 2, 4 });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void KindMismatch_Throws() =>
        Assert.Throws<InvalidOperationException>(() => PropertyValue.From(42).AsString);
}

public class WireExtensionsTests
{
    static PropertyValue Roundtrip(in PropertyValue v)
    {
        var w = new NetDataWriter();
        w.PutProperty(v);
        var r = new NetDataReader(w.CopyData());
        return r.GetProperty();
    }

    [Fact] public void PropertyValue_Null_RoundTrips() => Assert.True(Roundtrip(PropertyValue.Null).IsNull);
    [Fact] public void PropertyValue_Bool_RoundTrips() => Assert.True(Roundtrip(PropertyValue.From(true)).AsBool);
    [Fact] public void PropertyValue_Int_RoundTrips() => Assert.Equal(-999, Roundtrip(PropertyValue.From(-999)).AsInt);
    [Fact] public void PropertyValue_Long_RoundTrips() => Assert.Equal(long.MinValue, Roundtrip(PropertyValue.From(long.MinValue)).AsLong);
    [Fact] public void PropertyValue_Float_RoundTrips() => Assert.Equal(1.5f, Roundtrip(PropertyValue.From(1.5f)).AsFloat);
    [Fact] public void PropertyValue_Double_RoundTrips() => Assert.Equal(1.5, Roundtrip(PropertyValue.From(1.5)).AsDouble);
    [Fact] public void PropertyValue_String_RoundTrips() => Assert.Equal("日本語 🎮", Roundtrip(PropertyValue.From("日本語 🎮")).AsString);
    [Fact] public void PropertyValue_Bytes_RoundTrips() => Assert.Equal(new byte[] { 9, 8, 7 }, Roundtrip(PropertyValue.From(new byte[] { 9, 8, 7 })).AsBytes);

    [Fact]
    public void PropertyMap_RoundTrips()
    {
        var map = new Dictionary<string, PropertyValue>
        {
            ["a"] = PropertyValue.From(1),
            ["b"] = PropertyValue.From("hello"),
            ["c"] = PropertyValue.From(true),
        };
        var w = new NetDataWriter();
        w.PutPropertyMap(map);
        var r = new NetDataReader(w.CopyData());
        var decoded = r.GetPropertyMap();
        Assert.Equal(3, decoded.Count);
        Assert.Equal(1, decoded["a"].AsInt);
        Assert.Equal("hello", decoded["b"].AsString);
        Assert.True(decoded["c"].AsBool);
    }

    [Fact]
    public void OptString_Null_RoundTrips()
    {
        var w = new NetDataWriter();
        w.PutOptString(null);
        Assert.Null(new NetDataReader(w.CopyData()).GetOptString());
    }

    [Fact]
    public void OptString_NonNull_RoundTrips()
    {
        var w = new NetDataWriter();
        w.PutOptString("hi");
        Assert.Equal("hi", new NetDataReader(w.CopyData()).GetOptString());
    }

    [Fact]
    public void OptIntArray_Null_RoundTrips()
    {
        var w = new NetDataWriter();
        w.PutOptIntArray(null);
        Assert.Null(new NetDataReader(w.CopyData()).GetOptIntArray());
    }

    [Fact]
    public void OptIntArray_Values_RoundTrips()
    {
        var arr = new[] { 1, 2, 3, int.MinValue, int.MaxValue };
        var w = new NetDataWriter();
        w.PutOptIntArray(arr);
        Assert.Equal(arr, new NetDataReader(w.CopyData()).GetOptIntArray());
    }

    [Fact]
    public void IntIntMap_RoundTrips()
    {
        var m = new Dictionary<int, int> { [10] = 100, [20] = 200 };
        var w = new NetDataWriter();
        w.PutIntIntMap(m);
        var d = new NetDataReader(w.CopyData()).GetIntIntMap();
        Assert.Equal(m, d);
    }

    [Fact]
    public void Payload_RoundTrips()
    {
        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3, 4, 5 };
        var w = new NetDataWriter();
        w.PutPayload(payload);
        var d = new NetDataReader(w.CopyData()).GetPayload();
        Assert.Equal(payload.ToArray(), d);
    }
}

public class MessageCodecTests
{
    static Message Roundtrip(Message m)
    {
        var w = new NetDataWriter();
        MessageCodec.Write(w, m);
        return MessageCodec.Read(new NetDataReader(w.CopyData()));
    }

    [Fact]
    public void Hello_RoundTrips()
    {
        var m = new Hello(2, "alice", "some-token");
        var d = (Hello)Roundtrip(m);
        Assert.Equal(m.ProtocolVersion, d.ProtocolVersion);
        Assert.Equal(m.DesiredUserId, d.DesiredUserId);
        Assert.Equal(m.Token, d.Token);
    }

    [Fact]
    public void Hello_WithNulls_RoundTrips()
    {
        var m = new Hello(2, null, null);
        var d = (Hello)Roundtrip(m);
        Assert.Null(d.DesiredUserId);
        Assert.Null(d.Token);
    }

    [Fact]
    public void HelloAck_RoundTrips()
    {
        var m = new HelloAck("alice", 12345L, new Dictionary<byte, Dictionary<string, PropertyValue>>
        {
            [0] = new() { ["score"] = PropertyValue.From(100) },
            [1] = new() { ["chair"] = PropertyValue.From(new byte[] { 1, 2, 3 }) },
        }, "1.2.3+abcd");
        var d = (HelloAck)Roundtrip(m);
        Assert.Equal("alice", d.AssignedUserId);
        Assert.Equal(12345L, d.ServerTimeMs);
        Assert.Equal(100, d.PeerData[0]["score"].AsInt);
        Assert.Equal(new byte[] { 1, 2, 3 }, d.PeerData[1]["chair"].AsBytes);
        Assert.Equal("1.2.3+abcd", d.ServerBuildVersion);
    }

    [Fact]
    public void JoinInstanceAck_RoundTrips()
    {
        var m = new JoinInstanceAck(
            42, "room-1", 3, 1,
            new[] { 1, 2, 3 },
            new Dictionary<string, PropertyValue> { ["mode"] = PropertyValue.From("match") },
            new Dictionary<int, int> { [100] = 2 },
            98765L);
        var d = (JoinInstanceAck)Roundtrip(m);
        Assert.Equal(42u, d.RequestId);
        Assert.Equal("room-1", d.InstanceName);
        Assert.Equal(3, d.MyPeerId);
        Assert.Equal(1, d.MasterPeerId);
        Assert.Equal(new[] { 1, 2, 3 }, d.PeerIds);
        Assert.Equal("match", d.InstanceProperties["mode"].AsString);
        Assert.Equal(2, d.ObjectOwners[100]);
        Assert.Equal(98765L, d.ServerTimeMs);
    }

    [Fact]
    public void SendMessage_OpaquePayload_RoundTrips()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var m = new Shared.Messages.SendMessage(1, 42, Routing.Others, null, 0, CachePolicy.None,
            DeliveryMode.Sequenced, Channel: (byte)3, payload);
        var d = (Shared.Messages.SendMessage)Roundtrip(m);
        Assert.Equal(42, d.MessageCode);
        Assert.Equal(Routing.Others, d.Routing);
        Assert.Null(d.TargetPeers);
        Assert.Equal(DeliveryMode.Sequenced, d.Delivery);
        Assert.Equal(3, d.Channel);
        Assert.Equal(payload, d.Payload.ToArray());
    }

    [Fact]
    public void SendMessage_WithTargetPeers_RoundTrips()
    {
        var m = new Shared.Messages.SendMessage(2, 5, Routing.Peers, new[] { 3, 4 }, 0, CachePolicy.AddPerPeer,
            DeliveryMode.ReliableOrdered, Channel: (byte)1, Encoding.UTF8.GetBytes("hi"));
        var d = (Shared.Messages.SendMessage)Roundtrip(m);
        Assert.Equal(Routing.Peers, d.Routing);
        Assert.Equal(new[] { 3, 4 }, d.TargetPeers);
        Assert.Equal(CachePolicy.AddPerPeer, d.Cache);
        Assert.Equal((byte)1, d.Channel);
    }

    [Fact]
    public void SetProperty_WithCas_RoundTrips()
    {
        var m = new SetProperty(7, 5, "hp", PropertyValue.From(99),
            HasExpected: true, Expected: PropertyValue.From(100));
        var d = (SetProperty)Roundtrip(m);
        Assert.True(d.HasExpected);
        Assert.Equal(100, d.Expected.AsInt);
        Assert.Equal(99, d.Value.AsInt);
    }

    [Fact]
    public void SetProperty_WithoutCas_RoundTrips()
    {
        var m = new SetProperty(7, 0, "mode", PropertyValue.From("go"),
            HasExpected: false, Expected: PropertyValue.Null);
        var d = (SetProperty)Roundtrip(m);
        Assert.False(d.HasExpected);
    }

    [Fact]
    public void ObjectOwnerChanged_RoundTrips()
    {
        var m = new ObjectOwnerChanged(42, 3, 1);
        var d = (ObjectOwnerChanged)Roundtrip(m);
        Assert.Equal(42, d.NetworkId);
        Assert.Equal(3, d.Current);
        Assert.Equal(1, d.Previous);
    }

    [Fact]
    public void Error_RoundTrips()
    {
        var m = new Error(99, ErrorCode.CasMismatch, "no dice");
        var d = (Error)Roundtrip(m);
        Assert.Equal(99u, d.RequestId);
        Assert.Equal(ErrorCode.CasMismatch, d.Code);
        Assert.Equal("no dice", d.DebugMessage);
    }

    [Fact]
    public void Discriminator_Roundtrips_KeepConcreteType()
    {
        Assert.IsType<Ping>(Roundtrip(new Ping()));
        Assert.IsType<Pong>(Roundtrip(new Pong(0)));
        Assert.IsType<ServerTime>(Roundtrip(new ServerTime(0)));
        Assert.IsType<Congestion>(Roundtrip(new Congestion(true)));
        Assert.IsType<PeerJoined>(Roundtrip(new PeerJoined(1, [], [])));
        Assert.IsType<PeerLeft>(Roundtrip(new PeerLeft(1, false, 0)));
        Assert.IsType<LeaveInstance>(Roundtrip(new LeaveInstance(1, true)));
        Assert.IsType<LeaveInstanceAck>(Roundtrip(new LeaveInstanceAck(1)));
        Assert.IsType<SubscribeGroups>(Roundtrip(new SubscribeGroups(new byte[] { 5 }, Array.Empty<byte>())));
        Assert.IsType<IncomingMessage>(Roundtrip(new IncomingMessage(1, 2, DeliveryMode.ReliableOrdered, 0, new byte[] { 3 })));
        Assert.IsType<SetPeerData>(Roundtrip(new Shared.Messages.SetPeerData(1, 0, [])));
        Assert.IsType<SetPeerDataAck>(Roundtrip(new SetPeerDataAck(1, 0)));
        Assert.IsType<PeerDataChanged>(Roundtrip(new PeerDataChanged(1, 0, [])));
    }
}
