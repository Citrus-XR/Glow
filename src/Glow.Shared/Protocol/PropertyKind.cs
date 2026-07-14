using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Glow.Shared.Protocol
{
// PropertyValue variant tag. Deliberately narrow: enough to cover the
// primitives most sync layers care about without dragging in engine
// types at the wire level. Composite values (Vector3, Quaternion,
// Color, ...) live in SendMessage payload where they're opaque bytes
// the application encodes and decodes itself.
public enum PropertyKind : byte
{
    Null = 0,
    Bool = 1,
    Int = 2,      // int32
    Long = 3,     // int64
    Float = 4,    // float32
    Double = 5,   // float64
    String = 6,   // UTF-8
    Bytes = 7,    // opaque byte[]
}
}
