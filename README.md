# Glow

Fast in-memory relay server for real-time multiplayer applications.

- **Language**: C# / .NET 10 (NativeAOT-ready)
- **Transport**: LiteNetLib (UDP with reliable / unreliable / sequenced channels)
- **State**: 103 tests passing (3× stable), ~1.2M msg/s in 8×8 fanout on loopback

Glow is a self-hosted relay: a single binary that accepts client connections, groups them into named **instances**, and moves opaque byte payloads between them with a rich set of routing, ordering, and delivery guarantees. Servers arbitrate scene-object ownership, persist per-user data to disk, cache messages for late joiners, and surface backpressure signals when clients fall behind.

The wire protocol is fixed-schema and zero-boxing on the hot path; the server never inspects application payloads (they travel as opaque byte spans through a zero-copy broadcast).

---

## Table of contents

- [Concepts](#concepts)
- [Layout](#layout)
- [Quick start](#quick-start)
- [Server CLI](#server-cli)
- [Interactive CLI](#interactive-cli)
- [Wire protocol](#wire-protocol)
- [Programmatic use](#programmatic-use)
- [Payload helpers](#payload-helpers)
- [Performance](#performance)
- [Testing](#testing)
- [Non-goals](#non-goals)
- [Roadmap](#roadmap)

---

## Concepts

| Term | Meaning |
|---|---|
| **Instance** | A room / session. Every peer is in at most one at a time; the server can host many concurrently. |
| **Peer** | One connected participant. Identified by `PeerId` (`int`, monotonic within the instance, never reused). Backed by a `UserId` that survives disconnect/reconnect. |
| **Master** | The peer with the lowest `PeerId` in the instance. Auto-elected, migrates deterministically on leave/disconnect. |
| **PeerData** | Per-user key-value store persisted to a JSON file on disk. Survives sessions. Broadcast to instance peers on mutation. |
| **Object owner** | A `NetworkId → PeerId` map. Any peer may claim any id; the server serializes arrival order and supports CAS. When an owner leaves, ownership auto-transfers to the current master. |
| **Message** | Opaque byte payload sent by a peer with a `MessageCode`. Routed by `Routing` (Others / All / Master / Peers / Group) with per-send `DeliveryMode` and `Channel`. |
| **Interest group** | A byte tag peers can subscribe to. Senders can target a group; only subscribers receive. Group 0 is a permanent broadcast that every peer is implicitly in. |
| **Property** | Instance-scoped or peer-scoped typed value (`Null` / `Bool` / `Int` / `Long` / `Float` / `Double` / `String` / `Bytes`). Supports CAS on write. |
| **Message cache** | Ordered buffer per instance. Cached messages are replayed to late joiners immediately after their JoinInstanceAck, on the original sender's delivery mode + channel. |
| **Bandwidth budget** | Configurable per-session outbound byte-rate. When exceeded, unreliable payloads are dropped and a `Congestion` event is pushed to the client so its game loop can back off. |

---

## Layout

```
src/
├── Glow.Shared/          Protocol enums, message records, wire codec,
│                         PropertyValue, PayloadWriter / PayloadReader
│                         (netstandard2.1, C# 9 — Unity-compatible)
├── Glow.Client/          Reference client library (ClientConnection,
│                         ClientState)
│                         (netstandard2.1, C# 9 — Unity-compatible)
├── Glow.Server/          Instance / Peer / handlers / dispatcher / transport /
│                         persistence / status HTTP / bandwidth limiter
│                         (net10.0, NativeAOT-ready)
├── Glow.Cli/             Interactive REPL frontend built on Glow.Client
│                         (net10.0, NativeAOT-ready)
└── Glow.Bench/           Micro-benchmarks (wire codec, PropertyValue) +
                          end-to-end throughput driver (net10.0)
tests/
├── Glow.Shared.Tests/    Wire, PropertyValue, PayloadWriter/Reader,
│                         MessageCodec round-trip
└── Glow.Server.Tests/    Instance / MessageCache / ObjectOwner unit tests,
                          persistence tests, live-UDP integration tests
```

**Unity compatibility**: `Glow.Shared` and `Glow.Client` target `netstandard2.1` and are written in C# 9 (default Unity 2022.3 language level). Drop the compiled DLLs into `Assets/Plugins/`, or import the `.cs` sources directly — no Roslyn-analyzer, source-generator, or newer BCL dependency needed beyond what LiteNetLib brings.

---

## Quick start

Requires **.NET 10 SDK**.

```bash
# Restore + build
dotnet build

# Run the server (foreground)
dotnet run --project src/Glow.Server -c Release -- --port 1840
```

In a second terminal, start the REPL:

```bash
dotnet run --project src/Glow.Cli -c Release
> connect localhost:1840
> hello alice
> join default
> send 10 "hello everyone"
```

**Native AOT single-binary publish:**

```bash
dotnet publish src/Glow.Server -c Release -o publish/server
dotnet publish src/Glow.Cli    -c Release -o publish/cli
./publish/server/Glow.Server --port 1840
```

Two windows on the same host, or two hosts on the same network, will speak to each other via the server.

---

## Server CLI

```
Glow.Server [options]

  --port <n>          UDP port to bind (default: 1840)
  --key <s>           Connect key clients must present (default: glow)
  --instance <s>      Baseline instance created at startup (default: default)
  --no-instance       Do not create a baseline instance
  --status <url>      Status HTTP prefix (default: http://localhost:5155/)
  --no-status         Disable status HTTP listener
  --quiet, -q         Suppress per-event logs (connects, joins, ownership, sweeps)
  --help, -h          Show help
```

Per-event logs (on by default, hidden by `--quiet`):

- Peer connect / disconnect on the transport
- Instance join / rejoin / leave / go-inactive (`[Instance] '<name>' peer N (userId) joined, master=M, active=K`)
- Master migration on leave
- Object ownership changes — both explicit `SetObjectOwner` and leave-time transfer to the new master
- Empty-instance destruction after `EmptyInstanceTtl` elapses

Startup banners, shutdown notices, and errors are always printed regardless of `--quiet`.

**Status HTTP** (read-only introspection):

- `GET /` — hello + version + current server-time in ms
- `GET /state` — full JSON dump: sessions, instances, peers, master, cached messages, bandwidth stats, object owner map

---

## Interactive CLI

Full command list (`help` inside the REPL):

```
connect <host:port> [key]              Open a connection
disconnect                             Close it and reset local state
hello [userId]                         Handshake; server assigns UUID if omitted
join <instance> [existing|create|rejoin] [--claim 1,2,3]
leave [inactive]                       Leave the current instance

send <code> [data] [--delivery X] [--channel N]        routing = Others
send-all <code> [data] [--delivery X] [--channel N]    include sender
send-master <code> [data] [--delivery X] [--channel N] master only
send-to <a,b,c> <code> [data] [flags]                  explicit peers
send-group <n> <code> [data] [flags]                   by interest group

  --delivery: unreliable | sequenced | reliable |
              reliable-ordered | reliable-sequenced
              (aliases: u / s / r / ro / rs; default: ro)
  --channel:  0..15 (default 0)

setprop k=v                            Instance property
setpeerprop <peerId> k=v               Peer property
getprops                               Fetch instance + all peers
groups add|remove <n> [n...]           Subscribe / unsubscribe
own <netId> [expected]                 Claim scene-object ownership (CAS if expected given)
peerdata k=v [k=v...]                  Persistent per-user data (null value deletes)
peerdata-get                           Fetch self peer data
state                                  Print local mirror
sleep [ms]                             Wait (default 250)
help / quit
```

**Value literals** in commands:

| Input | Result |
|---|---|
| `null` | Null |
| `true` / `false` | Bool |
| Decimal integer (fits `int`) | Int |
| Larger decimal integer | Long |
| Number with `.` or `e` | Double |
| `0x<hex>` | Bytes |
| `"quoted string"` | String (preserves spaces, `\"` and `\\` escapes) |
| Everything else | String |

**Scripted mode** — one command per line, `#` for comments:

```bash
./Glow.Cli --script scripts/single-client-smoke.txt
```

---

## Wire protocol

Framing:

```
[byte MessageType][fixed-schema body]
```

No dictionary, no self-describing type tags on message fields — each `MessageType` maps to a concrete record with a fixed field order.

### Message types (single byte)

| # | Name | Direction |
|---|---|---|
| 1 | `Hello` | C → S |
| 2 | `HelloAck` | S → C |
| 3 | `Ping` | C → S |
| 4 | `Pong` | S → C |
| 5 | `Error` | S → C (with `RequestId` when correlated) |
| 10 | `JoinInstance` | C → S |
| 11 | `JoinInstanceAck` | S → C |
| 12 | `LeaveInstance` | C → S |
| 13 | `LeaveInstanceAck` | S → C |
| 14 | `PeerJoined` | S → C (notification) |
| 15 | `PeerLeft` | S → C (notification) |
| 20 | `SendMessage` | C → S |
| 21 | `IncomingMessage` | S → C |
| 30 | `SetProperty` | C → S |
| 31 | `SetPropertyAck` | S → C |
| 32 | `PropertyChanged` | S → C |
| 33 | `GetProperties` | C → S |
| 34 | `GetPropertiesAck` | S → C |
| 40 | `SubscribeGroups` | C → S (no response) |
| 50 | `SetObjectOwner` | C → S |
| 51 | `SetObjectOwnerAck` | S → C |
| 52 | `ObjectOwnerChanged` | S → C |
| 60 | `SetPeerData` | C → S |
| 61 | `SetPeerDataAck` | S → C |
| 62 | `GetPeerData` | C → S |
| 63 | `GetPeerDataAck` | S → C |
| 64 | `PeerDataChanged` | S → C |
| 70 | `ServerTime` | S → C |
| 71 | `Congestion` | S → C |

Every request-style message carries a client-allocated `uint RequestId`; the paired Ack (or `Error`) echoes it. Notifications have no request id.

`JoinInstance` accepts an optional `ClaimObjectIds: int[]?` — a preset list of network ids the joining peer wants to claim ownership of atomically at join time. Each id is CAS-claimed against "unowned" (already-owned ids skip silently), so it's safe for every peer to declare its baked-in scene ids on entry. Successful claims land in the returned `JoinInstanceAck.ObjectOwners` snapshot and produce one `ObjectOwnerChanged` broadcast to existing peers.

`JoinInstanceAck.ExistingPeersData` is a `Dictionary<int, Dictionary<byte, Dictionary<string, PropertyValue>>>` snapshot of every existing active peer's PeerData at ack time, keyed by `PeerId`. Late joiners rebuild the full remote-peer state atomically from the ack instead of stitching together a follow-up train of `PeerDataChanged` messages. Peers with no populated stores are omitted; the map is empty when the joiner arrives alone. Live mutations that land between the snapshot and the ack are delivered as normal `PeerDataChanged` after the join.

### DeliveryMode

Per-`SendMessage` field; server transports at that delivery mode; receiver sees it on `IncomingMessage.Delivery`.

| Value | Semantic |
|---|---|
| 0 `Unreliable` | Fire and forget. May be lost / duplicated / reordered. |
| 1 `Sequenced` | Unreliable; out-of-order packets are dropped so the receiver only ever sees the newest. Good for state sync (position, rotation). |
| 2 `Reliable` | ACKed and retransmitted; no ordering guarantee. |
| 3 `ReliableOrdered` | ACKed, delivered in send order. Default for RPCs. |
| 4 `ReliableSequenced` | ACKed, keep newest only. |

### Channel

`byte` in `[0, ChannelsCount)` (default 16). Per-channel ordering; independent channels do not head-of-line-block each other. Sender picks; server transports on the same channel; receiver sees the channel on `IncomingMessage.Channel`. Server rejects out-of-range channels with `ErrorCode.InvalidMessage`.

### Routing

| Value | Semantic |
|---|---|
| 0 `Others` | All active peers except the sender |
| 1 `All` | All active peers including the sender |
| 2 `Master` | Master peer only |
| 3 `Peers` | Explicit `int[] TargetPeers` |
| 4 `Group` | Peers subscribed to `byte InterestGroup` |

### CachePolicy

| Value | Semantic |
|---|---|
| 0 `None` | Do not cache |
| 1 `AddPerPeer` | Store in the sender's bucket; cleared on the sender's departure if `CleanupCacheOnLeave` |
| 2 `AddGlobal` | Store in the shared bucket; not evicted when a peer leaves |
| 3 `RemoveByCode` | Delete cached entries matching (MessageCode, sender) |
| 4 `RemoveDeparted` | Sweep buckets belonging to peers no longer active |

Late joiners receive every cached entry in insertion order after their `JoinInstanceAck` and before any live message.

### PropertyValue

Tagged union `struct` — primitives stay inline (no heap allocation, no boxing):

```
Null | Bool | Int (i32) | Long (i64) | Float (f32) | Double (f64) | String (UTF-8) | Bytes (opaque)
```

Equality is structural (`Ordinal` for `String`, byte-wise for `Bytes`) so `SetProperty` CAS behaves predictably.

### Payload

`SendMessage.Payload` is opaque `ReadOnlyMemory<byte>`. The server never parses it — the same bytes flow into every receiver's `IncomingMessage.Payload`. Encode / decode using [`PayloadWriter` / `PayloadReader`](#payload-helpers).

### ErrorCode

Grouped by domain:

```
    0  Ok
 1001  ProtocolMismatch
 1002  NotAuthenticated
 1003  InvalidMessage
 1004  RateLimited
 1100  InstanceNotFound
 1101  InstanceAlreadyExists
 1102  InstanceFull
 1103  InstanceClosed
 1104  NotInInstance
 1105  AlreadyInInstance
 1200  PeerAlreadyActive
 1201  PeerRejoinNotFound
 1300  CasMismatch
 1301  PropertyKeyInvalid
 1302  PropertyTargetMissing
 1303  PayloadTooLarge
```

---

## Programmatic use

```csharp
using Glow.Client;
using Glow.Shared;
using Glow.Shared.Messages;
using Glow.Shared.Protocol;

using var conn = new ClientConnection();
await conn.ConnectAsync("localhost", 1840, "glow", CancellationToken.None);

// Hello (HelloAck arrives as a notification)
conn.Fire(new Hello(Meta.ProtocolVersion, "alice", null));

// Join
var joinId = conn.AllocateRequestId();
var joinResp = await conn.SendRequest(joinId,
    new JoinInstance(joinId, "default", JoinMode.JoinOrCreate,
        new Dictionary<string, PropertyValue>()));
var join = (JoinInstanceAck)joinResp;
Console.WriteLine($"peer {join.MyPeerId}, master {join.MasterPeerId}");

// Send a typed payload
var payload = new PayloadWriter()
    .PutVec3(1.5f, 2f, 3f)
    .PutInt(playerHp)
    .ToPayload();
conn.Fire(new SendMessage(
    RequestId: 0, MessageCode: 42, Routing.Others,
    TargetPeers: null, InterestGroup: 0, CachePolicy.None,
    DeliveryMode.Sequenced, Channel: 3,
    payload));

// Receive
conn.OnNotification += msg =>
{
    switch (msg)
    {
        case IncomingMessage im when im.MessageCode == 42:
            var r = new PayloadReader(im.Payload);
            var (x, y, z) = r.GetVec3();
            var hp = r.GetInt();
            // dispatch by im.SenderPeerId / im.Channel
            break;
        case PeerJoined pj:
            Console.WriteLine($"peer {pj.PeerId} joined");
            break;
        case PeerLeft pl:
            Console.WriteLine($"peer {pl.PeerId} left; new master {pl.NewMasterPeerId}");
            break;
    }
};
```

---

## Payload helpers

`PayloadWriter` (chain-style put):

```
PutBool  PutByte  PutSByte  PutShort  PutUShort  PutInt  PutUInt
PutLong  PutULong PutFloat  PutDouble PutString  PutBytes
PutVec2  PutVec3  PutVec4   PutQuat   PutColor   PutColor32
PutIntArray  PutFloatArray  PutStringArray
```

`PayloadReader` (mirror). Wire format is raw fields in put order with no self-describing tags — sender and receiver agree on the layout per `MessageCode`.

```csharp
// Sender
var p = new PayloadWriter()
    .PutInt(playerId)
    .PutColor(1f, 0.5f, 0f, 1f)
    .PutStringArray(new[] { "chat", "line", "one" })
    .ToPayload();

// Receiver
var r = new PayloadReader(im.Payload);
var id = r.GetInt();
var (rC, gC, bC, aC) = r.GetColor();
var lines = r.GetStringArray();
```

---

## Performance

Measured on a modern workstation, .NET 10 Release, single-machine UDP loopback.

**Micro (wire codec, single thread):**

| Operation | ns / op | ops / sec |
|---|---:|---:|
| `Hello` encode + decode | 437 | 2.3 M |
| `HelloAck` (5-key PeerData) | 1310 | 763 k |
| `JoinInstanceAck` (3 peers, 4 props, 2 owners) | 1266 | 790 k |
| `SendMessage` 5 B payload | 209 | 4.8 M |
| `SendMessage` 1 KB payload | 343 | 2.9 M |
| `PropertyValue.Equals` (int) | 6.5 | 154 M |
| `PropertyValue.Equals` (string) | 10.6 | 94 M |

**End-to-end throughput (zero loss):**

| Scenario | msg / s | MB / s |
|---|---:|---:|
| 1 sender → 1 receiver, 32 B, `ReliableOrdered` | 191 k | 7.6 |
| 1 sender → 4 receivers, 32 B, `ReliableOrdered` | 435 k | 17.4 |
| 1 sender → 8 receivers, 32 B, `ReliableOrdered` | 681 k | 27.3 |
| 1 sender → 1 receiver, 256 B, `ReliableOrdered` | 34 k | 8.6 |
| 4 senders → 1 receiver, 32 B, `All` | 209 k | 8.4 |
| **8 senders × 8 receivers, 32 B, `ReliableOrdered`** | **1.24 M** | 49.6 |
| 8 senders × 8 receivers, 32 B, `Sequenced` | 424 k | 17.0 |

Run yourself:

```bash
dotnet run --project src/Glow.Bench -c Release            # all
dotnet run --project src/Glow.Bench -c Release -- --micro
dotnet run --project src/Glow.Bench -c Release -- --throughput
```

---

## Testing

```bash
dotnet test
```

103 tests total:

- **43 shared** — `PropertyValue` equality, `PayloadWriter` / `PayloadReader`, every `Message` type wire round-trip, discriminator-preserves-type
- **60 server** — `Instance` / `Peer` / `MessageCache` / `InstanceRegistry` / `ObjectOwners` unit tests, `PeerDataJsonCodec` + `PeerDataStore`, plus live-UDP integration:
  - 3+ client scenarios (peer list, master migration, cache replay)
  - Object ownership (claim, transfer, CAS mismatch, on-leave transfer, join snapshot)
  - Delivery + channel (echo, independent-ordered channels, out-of-range rejection, cache preserves original delivery/channel)
  - PeerData persistence across reconnect (single- and multi-key, null delete)
  - Bandwidth pressure, congestion event
  - Disconnect scenarios (mid-join, post-hello, rapid reconnect)
  - State-machine rejections (join before hello, join twice, leave without join, ...)
  - Interest group filter + unsubscribe
  - Property CAS, multi-instance isolation, server-time monotonicity

Test suite is stable across 3× consecutive runs; no flakes.

---

## Non-goals

- **NAT traversal** — bring your own mediator, or run on public IP.
- **Transport-layer encryption** — LiteNetLib supports CRC / XOR / AES layers; not wired into the default build.
- **Matchmaking / lobby / region routing** — a single relay serves one endpoint; front with your own directory if needed.
- **Wire-level compatibility with other networking libraries** — Glow's protocol is its own, deliberately not interoperable.
- **Client-authoritative validation** — Glow relays, arbitrates ordering, and enforces its own state-machine gates; application-level validation belongs in the app.

---

## Roadmap

- `SetProperty` broadcast filter (skip echo to sender when `BroadcastPropertyChangeToAll = false`)
- `EmptyInstanceTtl` reaper task to auto-destroy dormant instances
- Interest group snapshot on `JoinInstance` (currently only group 0 is baseline)
- Optional protocol-level encryption
- Fragmentation window tuning for large Unreliable payloads
- Structured server-side logging + OpenTelemetry hook
