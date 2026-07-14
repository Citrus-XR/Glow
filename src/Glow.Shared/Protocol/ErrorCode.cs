using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// Error codes carried on the generic Error message. 0 = ok; positive
// values are grouped by domain. Numbers are deliberately in the 1000+
// range — no relation to any prior library's error tables.
public static class ErrorCode
{
    public const short Ok = 0;

    // 1000-1099: session / framing
    public const short ProtocolMismatch = 1001;
    public const short NotAuthenticated = 1002;
    public const short InvalidMessage = 1003;
    public const short RateLimited = 1004;

    // 1100-1199: instance
    public const short InstanceNotFound = 1100;
    public const short InstanceAlreadyExists = 1101;
    public const short InstanceFull = 1102;
    public const short InstanceClosed = 1103;
    public const short NotInInstance = 1104;
    public const short AlreadyInInstance = 1105;

    // 1200-1299: peer slot
    public const short PeerAlreadyActive = 1200;   // same UserId, currently active
    public const short PeerRejoinNotFound = 1201;

    // 1300-1399: property / owner
    public const short CasMismatch = 1300;
    public const short PropertyKeyInvalid = 1301;
    public const short PropertyTargetMissing = 1302;
    public const short PayloadTooLarge = 1303;

    // 1400-1499: persistence
    // Store-scoped quota (PeerDataStore per-user per-tag byte budget)
    // exhausted. The mutation is dropped without partial application;
    // the caller sees this on SetPeerDataAck.ErrorCode.
    public const short QuotaExceeded = 1400;
}
}
