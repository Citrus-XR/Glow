using System.Collections.Generic;
using System.Linq;
using Glow.Shared;
using Glow.Shared.Messages;

namespace Glow.Client;

// Pretty-print a Message for the REPL log. Purely cosmetic.
public static class MessagePrinter
{
    public static string Format(Message m) => m switch
    {
        HelloAck a => $"HelloAck userId=\"{a.AssignedUserId}\" t={a.ServerTimeMs} peerData={FormatStoreCounts(a.PeerData)} serverBuild=\"{a.ServerBuildVersion}\"",
        JoinInstanceAck a => $"JoinInstanceAck instance=\"{a.InstanceName}\" self={a.MyPeerId} master={a.MasterPeerId} peers=[{string.Join(",", a.PeerIds)}] instanceProps={a.InstanceProperties.Count} owners={a.ObjectOwners.Count}",
        LeaveInstanceAck a => $"LeaveInstanceAck ok",
        PeerJoined a => $"PeerJoined peer={a.PeerId} props={a.Properties.Count} peerData={FormatStoreCounts(a.PeerData)}",
        PeerLeft a => $"PeerLeft peer={a.PeerId} inactive={a.BecameInactive} newMaster={a.NewMasterPeerId}",
        IncomingMessage a => $"IncomingMessage from={a.SenderPeerId} code={a.MessageCode} delivery={a.Delivery} ch={a.Channel} bytes={a.Payload.Length}",
        SetPropertyAck a => $"SetPropertyAck ok",
        GetPropertiesAck a => $"GetPropertiesAck instance={a.InstanceProperties.Count} peers={a.PeerProperties.Count}",
        PropertyChanged a => $"PropertyChanged target={a.TargetPeerId} \"{a.Key}\"={a.Value} by={a.ChangedBy}",
        SetObjectOwnerAck a => $"SetObjectOwnerAck netId={a.NetworkId} owner={a.Current} prev={a.Previous}",
        ObjectOwnerChanged a => $"ObjectOwnerChanged netId={a.NetworkId} owner={a.Current} prev={a.Previous}",
        SetPeerDataAck a => a.ErrorCode == 0 ? "SetPeerDataAck ok" : $"SetPeerDataAck error={a.ErrorCode}",
        GetPeerDataAck a => $"GetPeerDataAck stores={FormatStoreCounts(a.Data)}",
        PeerDataChanged a => $"PeerDataChanged peer={a.PeerId} store={a.Store} patchKeys={a.Patch.Count}",
        ServerTime a => $"ServerTime t={a.ServerTimeMs}",
        Congestion a => $"Congestion isClogged={a.IsClogged}",
        Error a => $"Error req={a.RequestId} code={a.Code} \"{a.DebugMessage}\"",
        _ => $"<{m.Type}>",
    };

    static string FormatStoreCounts(Dictionary<byte, Dictionary<string, PropertyValue>> stores) =>
        "{" + string.Join(",", stores.Select(kv => $"s{kv.Key}={kv.Value.Count}")) + "}";
}
