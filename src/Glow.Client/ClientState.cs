using Glow.Shared;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Client
{
// Local mirror of what the server has told us. Purely presentational -
// no protocol decisions read from here.
public sealed class ClientState
{
    public string? UserId { get; set; }
    public string? CurrentInstance { get; set; }
    public int? SelfPeerId { get; set; }
    public int? MasterPeerId { get; set; }
    public HashSet<int> KnownPeers { get; } = new();
    public Dictionary<string, PropertyValue> InstanceProperties { get; } = new();
    public Dictionary<int, int> ObjectOwners { get; } = new();
    public long? LastServerTimeMs { get; set; }
    public bool IsClogged { get; set; }
    // Server's Meta.BuildVersion, echoed back on HelloAck. Null before
    // Hello completes. Purely informational -- clients don't gate on it.
    public string? ServerBuildVersion { get; set; }
    // PeerData is a client-tagged namespace map. Outer byte is a store
    // tag freely chosen by the client (0..255); inner map is that tag's
    // property set. Each tag has its own server-side byte quota.
    public Dictionary<byte, Dictionary<string, PropertyValue>> PeerData { get; set; } = new();

    public bool IsInInstance => CurrentInstance is not null && SelfPeerId is not null;

    public void Reset()
    {
        UserId = null;
        IsClogged = false;
        ServerBuildVersion = null;
        PeerData = new();
        LeaveInstance();
    }

    public void LeaveInstance()
    {
        CurrentInstance = null;
        SelfPeerId = null;
        MasterPeerId = null;
        KnownPeers.Clear();
        InstanceProperties.Clear();
        ObjectOwners.Clear();
    }
}
}
