using Glow.Shared.Messages;
using Glow.Shared.Protocol;
using LiteNetLib.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Shared.Wire
{
// Top-level message codec. Write dispatches on concrete type via
// polymorphism; read dispatches on the leading MessageType byte. No
// reflection, so NativeAOT is happy.
public static class MessageCodec
{
    public static void Write(NetDataWriter w, Message message) => message.WriteTo(w);

    public static Message Read(NetDataReader r)
    {
        var type = (MessageType)r.GetByte();
        return type switch
        {
            MessageType.Hello => Hello.ReadFrom(r),
            MessageType.HelloAck => HelloAck.ReadFrom(r),
            MessageType.Ping => Ping.ReadFrom(r),
            MessageType.Pong => Pong.ReadFrom(r),
            MessageType.Error => Error.ReadFrom(r),

            MessageType.JoinInstance => JoinInstance.ReadFrom(r),
            MessageType.JoinInstanceAck => JoinInstanceAck.ReadFrom(r),
            MessageType.LeaveInstance => LeaveInstance.ReadFrom(r),
            MessageType.LeaveInstanceAck => LeaveInstanceAck.ReadFrom(r),
            MessageType.PeerJoined => PeerJoined.ReadFrom(r),
            MessageType.PeerLeft => PeerLeft.ReadFrom(r),

            MessageType.SendMessage => SendMessage.ReadFrom(r),
            MessageType.IncomingMessage => IncomingMessage.ReadFrom(r),
            MessageType.IncomingCachedMessage => IncomingCachedMessage.ReadFrom(r),

            MessageType.SetProperty => SetProperty.ReadFrom(r),
            MessageType.SetPropertyAck => SetPropertyAck.ReadFrom(r),
            MessageType.PropertyChanged => PropertyChanged.ReadFrom(r),
            MessageType.GetProperties => GetProperties.ReadFrom(r),
            MessageType.GetPropertiesAck => GetPropertiesAck.ReadFrom(r),

            MessageType.SubscribeGroups => SubscribeGroups.ReadFrom(r),

            MessageType.SetObjectOwner => SetObjectOwner.ReadFrom(r),
            MessageType.SetObjectOwnerAck => SetObjectOwnerAck.ReadFrom(r),
            MessageType.ObjectOwnerChanged => ObjectOwnerChanged.ReadFrom(r),

            MessageType.SetPeerData => SetPeerData.ReadFrom(r),
            MessageType.SetPeerDataAck => SetPeerDataAck.ReadFrom(r),
            MessageType.GetPeerData => GetPeerData.ReadFrom(r),
            MessageType.GetPeerDataAck => GetPeerDataAck.ReadFrom(r),
            MessageType.PeerDataChanged => PeerDataChanged.ReadFrom(r),

            MessageType.ServerTime => ServerTime.ReadFrom(r),
            MessageType.Congestion => Congestion.ReadFrom(r),

            _ => throw new InvalidOperationException($"Unknown MessageType {(byte)type}"),
        };
    }
}
}
