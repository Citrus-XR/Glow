using LiteNetLib;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Shared.Protocol
{
// Per-SendMessage ordering & reliability. Mirrors what modern reliable-UDP
// transports offer. Applications pick per message: e.g. position updates
// = Sequenced, RPC = ReliableOrdered, one-shot broadcasts = Unreliable.
public enum DeliveryMode : byte
{
    // Fire-and-forget. May be lost / duplicated / reordered.
    Unreliable = 0,

    // Unreliable but out-of-order packets are dropped so the receiver
    // only ever sees the newest for this channel. Great for state sync
    // (position, rotation) where old snapshots are worthless.
    Sequenced = 1,

    // Retransmitted until acknowledged. No ordering guarantee across
    // packets on the channel.
    Reliable = 2,

    // Retransmitted and delivered in strict send order. Default choice
    // for RPC-style messages that must not tear.
    ReliableOrdered = 3,

    // Retransmitted, but the receiver only sees the newest ordering-wise.
    // Rarely useful; keep for completeness.
    ReliableSequenced = 4,
}

// Bridge Glow's public enum to the transport primitive without leaking
// LiteNetLib into the public API.
public static class DeliveryModeExtensions
{
    public static DeliveryMethod ToTransport(this DeliveryMode mode) => mode switch
    {
        DeliveryMode.Unreliable => DeliveryMethod.Unreliable,
        DeliveryMode.Sequenced => DeliveryMethod.Sequenced,
        DeliveryMode.Reliable => DeliveryMethod.ReliableUnordered,
        DeliveryMode.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        DeliveryMode.ReliableSequenced => DeliveryMethod.ReliableSequenced,
        _ => DeliveryMethod.ReliableOrdered,
    };
}
}
