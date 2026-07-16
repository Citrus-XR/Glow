using Glow.Shared.Protocol;
using Glow.Shared.Wire;
using LiteNetLib.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Shared.Messages
{
// Base class for every wire message. Each subclass owns its own binary
// layout via WriteTo; MessageCodec dispatches on the leading MessageType
// byte for reads. No parameter dictionary anywhere - every field has a
// concrete type and a fixed position.
public abstract record Message
{
    public abstract MessageType Type { get; }
    public abstract void WriteTo(NetDataWriter w);
}

// -----------------------------------------------------------------
// SESSION
// -----------------------------------------------------------------

public sealed record Hello(int ProtocolVersion, string? DesiredUserId, string? Token) : Message
{
    public override MessageType Type => MessageType.Hello;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(ProtocolVersion);
        w.PutOptString(DesiredUserId);
        w.PutOptString(Token);
    }
    public static Hello ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetOptString(), r.GetOptString());
}

// PeerData snapshot messages carry every populated store tag at once.
// Tags are byte namespaces chosen by the client (see SetPeerData). Empty
// tags are omitted. ServerBuildVersion is the InformationalVersion
// stamped into the server's Glow.Shared assembly at publish time
// (Meta.BuildVersion); "0.0.0-local" when built without CI's
// -p:InformationalVersion flag.
public sealed record HelloAck(
    string AssignedUserId,
    long ServerTimeMs,
    Dictionary<byte, Dictionary<string, PropertyValue>> PeerData,
    string ServerBuildVersion) : Message
{
    public override MessageType Type => MessageType.HelloAck;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(AssignedUserId);
        w.Put(ServerTimeMs);
        w.PutStorePropertyMap(PeerData);
        w.Put(ServerBuildVersion);
    }
    public static HelloAck ReadFrom(NetDataReader r) =>
        new(r.GetString(), r.GetLong(), r.GetStorePropertyMap(), r.GetString());
}

public sealed record Ping() : Message
{
    public override MessageType Type => MessageType.Ping;
    public override void WriteTo(NetDataWriter w) => w.Put((byte)Type);
    public static Ping ReadFrom(NetDataReader _) => new();
}

public sealed record Pong(long ServerTimeMs) : Message
{
    public override MessageType Type => MessageType.Pong;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(ServerTimeMs); }
    public static Pong ReadFrom(NetDataReader r) => new(r.GetLong());
}

public sealed record Error(uint RequestId, short Code, string DebugMessage) : Message
{
    public override MessageType Type => MessageType.Error;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(Code);
        w.Put(DebugMessage);
    }
    public static Error ReadFrom(NetDataReader r) =>
        new(r.GetUInt(), r.GetShort(), r.GetString());
}

// -----------------------------------------------------------------
// INSTANCE MEMBERSHIP
// -----------------------------------------------------------------

public sealed record JoinInstance(
    uint RequestId,
    string InstanceName,
    JoinMode Mode,
    Dictionary<string, PropertyValue> Properties) : Message
{
    // Optional: object ids the joining peer wants to claim ownership of
    // atomically at join time. Each id is attempted with CAS on "unowned"
    // (expected = 0) so a race with an existing owner is a silent skip,
    // not an overwrite. Successful claims are visible in the returned
    // JoinInstanceAck.ObjectOwners snapshot and broadcast to other peers
    // as ObjectOwnerChanged notifications.
    public int[]? ClaimObjectIds { get; init; }

    public override MessageType Type => MessageType.JoinInstance;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(InstanceName);
        w.Put((byte)Mode);
        w.PutPropertyMap(Properties);
        w.PutOptIntArray(ClaimObjectIds);
    }
    public static JoinInstance ReadFrom(NetDataReader r) => new(
        r.GetUInt(), r.GetString(), (JoinMode)r.GetByte(), r.GetPropertyMap())
    {
        ClaimObjectIds = r.GetOptIntArray(),
    };
}

public sealed record JoinInstanceAck(
    uint RequestId,
    string InstanceName,
    int MyPeerId,
    int MasterPeerId,
    int[] PeerIds,
    Dictionary<string, PropertyValue> InstanceProperties,
    Dictionary<int, int> ObjectOwners,
    long ServerTimeMs,
    // Every existing active peer's PeerData snapshot captured at ack
    // time, keyed by PeerId. The inner map mirrors the store-tagged
    // shape carried by HelloAck / PeerJoined / GetPeerDataAck so the
    // joiner can rebuild remote state in one atomic step -- no need
    // to wait for a train of PeerDataChanged messages. Peers with no
    // populated stores are omitted; the map is empty when the joiner
    // is alone. Concurrent SetPeerData calls that land between the
    // snapshot and the ack arriving are still delivered as normal
    // PeerDataChanged notifications after the join.
    Dictionary<int, Dictionary<byte, Dictionary<string, PropertyValue>>> ExistingPeersData) : Message
{
    public override MessageType Type => MessageType.JoinInstanceAck;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(InstanceName);
        w.Put(MyPeerId);
        w.Put(MasterPeerId);
        w.PutOptIntArray(PeerIds);
        w.PutPropertyMap(InstanceProperties);
        w.PutIntIntMap(ObjectOwners);
        w.Put(ServerTimeMs);
        w.PutPeerStorePropertyMap(ExistingPeersData);
    }
    public static JoinInstanceAck ReadFrom(NetDataReader r) => new(
        r.GetUInt(),
        r.GetString(),
        r.GetInt(),
        r.GetInt(),
        r.GetOptIntArray() ?? Array.Empty<int>(),
        r.GetPropertyMap(),
        r.GetIntIntMap(),
        r.GetLong(),
        r.GetPeerStorePropertyMap());
}

public sealed record LeaveInstance(uint RequestId, bool BecomeInactive) : Message
{
    public override MessageType Type => MessageType.LeaveInstance;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(BecomeInactive);
    }
    public static LeaveInstance ReadFrom(NetDataReader r) => new(r.GetUInt(), r.GetBool());
}

public sealed record LeaveInstanceAck(uint RequestId) : Message
{
    public override MessageType Type => MessageType.LeaveInstanceAck;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(RequestId); }
    public static LeaveInstanceAck ReadFrom(NetDataReader r) => new(r.GetUInt());
}

public sealed record PeerJoined(
    int PeerId,
    Dictionary<string, PropertyValue> Properties,
    Dictionary<byte, Dictionary<string, PropertyValue>> PeerData) : Message
{
    public override MessageType Type => MessageType.PeerJoined;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(PeerId);
        w.PutPropertyMap(Properties);
        w.PutStorePropertyMap(PeerData);
    }
    public static PeerJoined ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetPropertyMap(), r.GetStorePropertyMap());
}

public sealed record PeerLeft(int PeerId, bool BecameInactive, int NewMasterPeerId) : Message
{
    public override MessageType Type => MessageType.PeerLeft;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(PeerId);
        w.Put(BecameInactive);
        w.Put(NewMasterPeerId);
    }
    public static PeerLeft ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetBool(), r.GetInt());
}

// -----------------------------------------------------------------
// MESSAGING (opaque payload; server relays byte-for-byte)
// -----------------------------------------------------------------

public sealed record SendMessage(
    uint RequestId,
    byte MessageCode,
    Routing Routing,
    int[]? TargetPeers,
    byte InterestGroup,
    CachePolicy Cache,
    DeliveryMode Delivery,
    byte Channel,
    ReadOnlyMemory<byte> Payload,
    int CacheKey = 0) : Message
{
    public override MessageType Type => MessageType.SendMessage;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(MessageCode);
        w.Put((byte)Routing);
        w.PutOptIntArray(TargetPeers);
        w.Put(InterestGroup);
        w.Put((byte)Cache);
        w.Put((byte)Delivery);
        w.Put(Channel);
        w.PutPayload(Payload);
        w.Put(CacheKey);
    }
    public static SendMessage ReadFrom(NetDataReader r) => new(
        r.GetUInt(),
        r.GetByte(),
        (Routing)r.GetByte(),
        r.GetOptIntArray(),
        r.GetByte(),
        (CachePolicy)r.GetByte(),
        (DeliveryMode)r.GetByte(),
        r.GetByte(),
        r.GetPayload(),
        r.GetInt());
}

public sealed record IncomingMessage(
    int SenderPeerId,
    byte MessageCode,
    DeliveryMode Delivery,
    byte Channel,
    ReadOnlyMemory<byte> Payload) : Message
{
    public override MessageType Type => MessageType.IncomingMessage;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(SenderPeerId);
        w.Put(MessageCode);
        w.Put((byte)Delivery);
        w.Put(Channel);
        w.PutPayload(Payload);
    }
    public static IncomingMessage ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetByte(), (DeliveryMode)r.GetByte(), r.GetByte(), r.GetPayload());
}

// Emitted only from the join-time cache replay loop. Wire shape is
// identical to IncomingMessage — the type-byte discriminator is the
// storage-origin signal.
public sealed record IncomingCachedMessage(
    int SenderPeerId,
    byte MessageCode,
    DeliveryMode Delivery,
    byte Channel,
    ReadOnlyMemory<byte> Payload) : Message
{
    public override MessageType Type => MessageType.IncomingCachedMessage;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(SenderPeerId);
        w.Put(MessageCode);
        w.Put((byte)Delivery);
        w.Put(Channel);
        w.PutPayload(Payload);
    }
    public static IncomingCachedMessage ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetByte(), (DeliveryMode)r.GetByte(), r.GetByte(), r.GetPayload());
}

// -----------------------------------------------------------------
// PROPERTIES (Peer + Instance, CAS-supported)
// -----------------------------------------------------------------

// TargetPeerId = 0 addresses the instance's own properties. Otherwise it
// addresses a specific peer. HasExpected + Expected implement CAS: if the
// current value differs from Expected, the write is rejected.
public sealed record SetProperty(
    uint RequestId,
    int TargetPeerId,
    string Key,
    PropertyValue Value,
    bool HasExpected,
    PropertyValue Expected) : Message
{
    public override MessageType Type => MessageType.SetProperty;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(TargetPeerId);
        w.Put(Key);
        w.PutProperty(Value);
        w.Put(HasExpected);
        if (HasExpected) w.PutProperty(Expected);
    }
    public static SetProperty ReadFrom(NetDataReader r)
    {
        var reqId = r.GetUInt();
        var target = r.GetInt();
        var key = r.GetString();
        var val = r.GetProperty();
        var hasExpected = r.GetBool();
        var expected = hasExpected ? r.GetProperty() : PropertyValue.Null;
        return new SetProperty(reqId, target, key, val, hasExpected, expected);
    }
}

public sealed record SetPropertyAck(uint RequestId) : Message
{
    public override MessageType Type => MessageType.SetPropertyAck;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(RequestId); }
    public static SetPropertyAck ReadFrom(NetDataReader r) => new(r.GetUInt());
}

public sealed record PropertyChanged(
    int TargetPeerId,
    string Key,
    PropertyValue Value,
    int ChangedBy) : Message
{
    public override MessageType Type => MessageType.PropertyChanged;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(TargetPeerId);
        w.Put(Key);
        w.PutProperty(Value);
        w.Put(ChangedBy);
    }
    public static PropertyChanged ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetString(), r.GetProperty(), r.GetInt());
}

public sealed record GetProperties(
    uint RequestId,
    bool IncludeInstance,
    bool IncludePeers,
    int[]? TargetPeers) : Message
{
    public override MessageType Type => MessageType.GetProperties;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(IncludeInstance);
        w.Put(IncludePeers);
        w.PutOptIntArray(TargetPeers);
    }
    public static GetProperties ReadFrom(NetDataReader r) =>
        new(r.GetUInt(), r.GetBool(), r.GetBool(), r.GetOptIntArray());
}

public sealed record GetPropertiesAck(
    uint RequestId,
    Dictionary<string, PropertyValue> InstanceProperties,
    Dictionary<int, Dictionary<string, PropertyValue>> PeerProperties) : Message
{
    public override MessageType Type => MessageType.GetPropertiesAck;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.PutPropertyMap(InstanceProperties);
        w.Put((ushort)PeerProperties.Count);
        foreach (var kv in PeerProperties)
        {
            w.Put(kv.Key);
            w.PutPropertyMap(kv.Value);
        }
    }
    public static GetPropertiesAck ReadFrom(NetDataReader r)
    {
        var reqId = r.GetUInt();
        var instance = r.GetPropertyMap();
        var peerCount = r.GetUShort();
        var peers = new Dictionary<int, Dictionary<string, PropertyValue>>(peerCount);
        for (var i = 0; i < peerCount; i++)
        {
            var peerId = r.GetInt();
            var map = r.GetPropertyMap();
            peers[peerId] = map;
        }
        return new GetPropertiesAck(reqId, instance, peers);
    }
}

// -----------------------------------------------------------------
// INTEREST GROUPS
// -----------------------------------------------------------------

// Add and Remove are byte arrays of group ids (1-255). Group 0 is a
// permanent broadcast group and cannot be modified. A zero-length array
// means "no change" for that direction.
public sealed record SubscribeGroups(byte[] Add, byte[] Remove) : Message
{
    public override MessageType Type => MessageType.SubscribeGroups;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.PutBytesWithLength(Add);
        w.PutBytesWithLength(Remove);
    }
    public static SubscribeGroups ReadFrom(NetDataReader r) =>
        new(r.GetBytesWithLength(), r.GetBytesWithLength());
}

// -----------------------------------------------------------------
// OBJECT OWNERSHIP
// -----------------------------------------------------------------

// NewOwner = 0 releases ownership. HasExpected + Expected implement CAS
// on the current owner value.
public sealed record SetObjectOwner(
    uint RequestId,
    int NetworkId,
    int NewOwner,
    bool HasExpected,
    int Expected) : Message
{
    public override MessageType Type => MessageType.SetObjectOwner;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(NetworkId);
        w.Put(NewOwner);
        w.Put(HasExpected);
        if (HasExpected) w.Put(Expected);
    }
    public static SetObjectOwner ReadFrom(NetDataReader r)
    {
        var reqId = r.GetUInt();
        var nid = r.GetInt();
        var newOwner = r.GetInt();
        var hasExpected = r.GetBool();
        var expected = hasExpected ? r.GetInt() : 0;
        return new SetObjectOwner(reqId, nid, newOwner, hasExpected, expected);
    }
}

public sealed record SetObjectOwnerAck(
    uint RequestId,
    int NetworkId,
    int Current,
    int Previous) : Message
{
    public override MessageType Type => MessageType.SetObjectOwnerAck;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(NetworkId);
        w.Put(Current);
        w.Put(Previous);
    }
    public static SetObjectOwnerAck ReadFrom(NetDataReader r) =>
        new(r.GetUInt(), r.GetInt(), r.GetInt(), r.GetInt());
}

public sealed record ObjectOwnerChanged(int NetworkId, int Current, int Previous) : Message
{
    public override MessageType Type => MessageType.ObjectOwnerChanged;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(NetworkId);
        w.Put(Current);
        w.Put(Previous);
    }
    public static ObjectOwnerChanged ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetInt(), r.GetInt());
}

// -----------------------------------------------------------------
// PEER DATA (persistent per-user, byte-tagged namespaces)
// -----------------------------------------------------------------

// PeerData is a per-user server-side store organized as a
// Dictionary<byte, Dictionary<string, PropertyValue>>. The outer byte
// is a "store tag" chosen by the client (any value 0..255) and treated
// as an opaque namespace by the server -- one game might use tag 0 for
// hot metadata and tag 1 for a serialized state blob, another might
// use only tag 0. Each tag has an independent byte-size quota
// enforced server-side (see PeerDataStore.PerStoreQuotaBytes).

// Patch: null values delete keys. Others merge. Store picks which tag
// namespace the patch targets. Over-quota writes come back with
// SetPeerDataAck.ErrorCode = QuotaExceeded and are dropped in full.
public sealed record SetPeerData(uint RequestId, byte Store, Dictionary<string, PropertyValue> Patch) : Message
{
    public override MessageType Type => MessageType.SetPeerData;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.Put(Store);
        w.PutPropertyMap(Patch);
    }
    public static SetPeerData ReadFrom(NetDataReader r) =>
        new(r.GetUInt(), r.GetByte(), r.GetPropertyMap());
}

public sealed record SetPeerDataAck(uint RequestId, short ErrorCode) : Message
{
    public override MessageType Type => MessageType.SetPeerDataAck;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(RequestId); w.Put(ErrorCode); }
    public static SetPeerDataAck ReadFrom(NetDataReader r) => new(r.GetUInt(), r.GetShort());
}

public sealed record GetPeerData(uint RequestId) : Message
{
    public override MessageType Type => MessageType.GetPeerData;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(RequestId); }
    public static GetPeerData ReadFrom(NetDataReader r) => new(r.GetUInt());
}

public sealed record GetPeerDataAck(uint RequestId, Dictionary<byte, Dictionary<string, PropertyValue>> Data) : Message
{
    public override MessageType Type => MessageType.GetPeerDataAck;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(RequestId);
        w.PutStorePropertyMap(Data);
    }
    public static GetPeerDataAck ReadFrom(NetDataReader r) =>
        new(r.GetUInt(), r.GetStorePropertyMap());
}

public sealed record PeerDataChanged(int PeerId, byte Store, Dictionary<string, PropertyValue> Patch) : Message
{
    public override MessageType Type => MessageType.PeerDataChanged;
    public override void WriteTo(NetDataWriter w)
    {
        w.Put((byte)Type);
        w.Put(PeerId);
        w.Put(Store);
        w.PutPropertyMap(Patch);
    }
    public static PeerDataChanged ReadFrom(NetDataReader r) =>
        new(r.GetInt(), r.GetByte(), r.GetPropertyMap());
}

// -----------------------------------------------------------------
// SERVER SIGNALS
// -----------------------------------------------------------------

public sealed record ServerTime(long ServerTimeMs) : Message
{
    public override MessageType Type => MessageType.ServerTime;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(ServerTimeMs); }
    public static ServerTime ReadFrom(NetDataReader r) => new(r.GetLong());
}

public sealed record Congestion(bool IsClogged) : Message
{
    public override MessageType Type => MessageType.Congestion;
    public override void WriteTo(NetDataWriter w) { w.Put((byte)Type); w.Put(IsClogged); }
    public static Congestion ReadFrom(NetDataReader r) => new(r.GetBool());
}
}
