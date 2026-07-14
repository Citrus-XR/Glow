using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// Behaviour selector for JoinInstance.
public enum JoinMode : byte
{
    JoinExisting = 0,   // instance must exist; otherwise ErrorCode.InstanceNotFound
    JoinOrCreate = 1,   // create instance if it does not exist
    RejoinOnly = 2,     // reclaim an inactive peer slot with the same UserId; otherwise fail
}
}
