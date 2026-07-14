using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// Top-level wire discriminator. One byte per message; grouped by concern
// with gaps so future additions land next to related messages. Values are
// deliberately small integers (1-99) — no relation to any prior library's
// numeric conventions.
public enum MessageType : byte
{
    // Session
    Hello = 1,
    HelloAck = 2,
    Ping = 3,
    Pong = 4,
    Error = 5,

    // Instance membership
    JoinInstance = 10,
    JoinInstanceAck = 11,
    LeaveInstance = 12,
    LeaveInstanceAck = 13,
    PeerJoined = 14,
    PeerLeft = 15,

    // Peer-to-peer messaging (opaque payload, server does not parse)
    SendMessage = 20,
    IncomingMessage = 21,
    // Same shape as IncomingMessage, but the server only emits this envelope
    // when replaying the join-time message cache. Lets the receiver
    // distinguish "cached message replayed on join" from a live send so
    // the app can react differently (e.g. skip a spawn animation).
    IncomingCachedMessage = 22,

    // Properties (peer + instance scoped, CAS-supported)
    SetProperty = 30,
    SetPropertyAck = 31,
    PropertyChanged = 32,
    GetProperties = 33,
    GetPropertiesAck = 34,

    // Interest group subscription (server-side receiver-filter)
    SubscribeGroups = 40,

    // Object ownership (scene / network-id owner map)
    SetObjectOwner = 50,
    SetObjectOwnerAck = 51,
    ObjectOwnerChanged = 52,

    // Persistent per-user data
    SetPeerData = 60,
    SetPeerDataAck = 61,
    GetPeerData = 62,
    GetPeerDataAck = 63,
    PeerDataChanged = 64,

    // Server-initiated signals
    ServerTime = 70,
    Congestion = 71,
}
}
