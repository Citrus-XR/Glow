using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// Polyfill for init-only setters (C# 9 records) on netstandard2.1.
// The compiler emits references to this type when it encounters `init`
// or record positional syntax; supplying our own copy lets both work
// without pulling in a newer runtime.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
