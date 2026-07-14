using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// SendMessage routing selector. Determines the receiver set on the server
// side. Priority is explicit — the client picks exactly one Routing value
// per SendMessage; there's no implicit ordering to remember.
public enum Routing : byte
{
    Others = 0,   // every active peer in the instance except the sender
    All = 1,      // every active peer including the sender
    Master = 2,   // the current master peer only
    Peers = 3,    // explicit int[] of peer ids (via TargetPeers)
    Group = 4,    // every peer subscribed to the given interest group
}
}
