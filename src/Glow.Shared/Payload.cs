using LiteNetLib.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Shared
{
// User-payload writer/reader. SendMessage.Payload is an opaque byte slice
// the server never parses; PayloadWriter/PayloadReader let the client
// pack typed data into it without pulling in a separate serialization lib.
// Types intentionally cover primitives, Vector2/3/4, Quaternion, Color
// (float RGBA), Color32 (byte RGBA), and arrays of primitives. For
// anything outside this set, use PutBytes / GetBytes and encode structure
// yourself.
//
// PayloadWriter is a class (not a struct) so `new PayloadWriter()` behaves
// consistently across C# 9 (Unity 2022.3 default) and newer versions.
public sealed class PayloadWriter
{
    readonly NetDataWriter _w;

    public PayloadWriter() { _w = new NetDataWriter(); }
    public PayloadWriter(int initialCapacity) { _w = new NetDataWriter(true, initialCapacity); }

    public int Length => _w.Length;

    // Snapshot the accumulated bytes. Safe to hand to SendMessage.Payload;
    // the returned array is a copy, so the writer can be reused after.
    public ReadOnlyMemory<byte> ToPayload() => _w.CopyData();

    public PayloadWriter PutBool(bool v) { _w.Put(v); return this; }
    public PayloadWriter PutByte(byte v) { _w.Put(v); return this; }
    public PayloadWriter PutSByte(sbyte v) { _w.Put(v); return this; }
    public PayloadWriter PutShort(short v) { _w.Put(v); return this; }
    public PayloadWriter PutUShort(ushort v) { _w.Put(v); return this; }
    public PayloadWriter PutInt(int v) { _w.Put(v); return this; }
    public PayloadWriter PutUInt(uint v) { _w.Put(v); return this; }
    public PayloadWriter PutLong(long v) { _w.Put(v); return this; }
    public PayloadWriter PutULong(ulong v) { _w.Put(v); return this; }
    public PayloadWriter PutFloat(float v) { _w.Put(v); return this; }
    public PayloadWriter PutDouble(double v) { _w.Put(v); return this; }
    public PayloadWriter PutString(string v) { _w.Put(v); return this; }

    // 4-byte length-prefixed byte block. For arbitrary opaque data.
    public PayloadWriter PutBytes(ReadOnlySpan<byte> v)
    {
        _w.Put(v.Length);
        _w.Put(v);
        return this;
    }

    // Unity-shaped composite types. Order matches most engines: XYZ / XYZW / RGBA.
    public PayloadWriter PutVec2(float x, float y) { _w.Put(x); _w.Put(y); return this; }
    public PayloadWriter PutVec3(float x, float y, float z) { _w.Put(x); _w.Put(y); _w.Put(z); return this; }
    public PayloadWriter PutVec4(float x, float y, float z, float w) { _w.Put(x); _w.Put(y); _w.Put(z); _w.Put(w); return this; }
    public PayloadWriter PutQuat(float x, float y, float z, float w) { _w.Put(x); _w.Put(y); _w.Put(z); _w.Put(w); return this; }
    public PayloadWriter PutColor(float r, float g, float b, float a) { _w.Put(r); _w.Put(g); _w.Put(b); _w.Put(a); return this; }
    public PayloadWriter PutColor32(byte r, byte g, byte b, byte a) { _w.Put(r); _w.Put(g); _w.Put(b); _w.Put(a); return this; }

    // Fixed-length arrays (length header + values). For readers that need
    // to iterate, GetIntArray etc. return the concrete typed array.
    public PayloadWriter PutIntArray(ReadOnlySpan<int> v)
    {
        _w.Put(v.Length);
        for (var i = 0; i < v.Length; i++) _w.Put(v[i]);
        return this;
    }
    public PayloadWriter PutFloatArray(ReadOnlySpan<float> v)
    {
        _w.Put(v.Length);
        for (var i = 0; i < v.Length; i++) _w.Put(v[i]);
        return this;
    }
    public PayloadWriter PutStringArray(ReadOnlySpan<string> v)
    {
        _w.Put(v.Length);
        for (var i = 0; i < v.Length; i++) _w.Put(v[i]);
        return this;
    }
}

// Mirror of PayloadWriter for the receive side. Callers wrap the byte
// slice they got from IncomingMessage.Payload and read fields in the
// same order the sender wrote them.
public ref struct PayloadReader
{
    readonly NetDataReader _r;

    public PayloadReader(ReadOnlyMemory<byte> payload) => _r = new NetDataReader(payload.ToArray());

    public int Position => _r.Position;
    public int AvailableBytes => _r.AvailableBytes;
    public bool EndOfData => _r.EndOfData;

    public bool GetBool() => _r.GetBool();
    public byte GetByte() => _r.GetByte();
    public sbyte GetSByte() => _r.GetSByte();
    public short GetShort() => _r.GetShort();
    public ushort GetUShort() => _r.GetUShort();
    public int GetInt() => _r.GetInt();
    public uint GetUInt() => _r.GetUInt();
    public long GetLong() => _r.GetLong();
    public ulong GetULong() => _r.GetULong();
    public float GetFloat() => _r.GetFloat();
    public double GetDouble() => _r.GetDouble();
    public string GetString() => _r.GetString();

    public byte[] GetBytes()
    {
        var len = _r.GetInt();
        var buf = new byte[len];
        _r.GetBytes(buf, len);
        return buf;
    }

    public (float X, float Y) GetVec2() => (_r.GetFloat(), _r.GetFloat());
    public (float X, float Y, float Z) GetVec3() => (_r.GetFloat(), _r.GetFloat(), _r.GetFloat());
    public (float X, float Y, float Z, float W) GetVec4() =>
        (_r.GetFloat(), _r.GetFloat(), _r.GetFloat(), _r.GetFloat());
    public (float X, float Y, float Z, float W) GetQuat() =>
        (_r.GetFloat(), _r.GetFloat(), _r.GetFloat(), _r.GetFloat());
    public (float R, float G, float B, float A) GetColor() =>
        (_r.GetFloat(), _r.GetFloat(), _r.GetFloat(), _r.GetFloat());
    public (byte R, byte G, byte B, byte A) GetColor32() =>
        (_r.GetByte(), _r.GetByte(), _r.GetByte(), _r.GetByte());

    public int[] GetIntArray()
    {
        var len = _r.GetInt();
        var a = new int[len];
        for (var i = 0; i < len; i++) a[i] = _r.GetInt();
        return a;
    }
    public float[] GetFloatArray()
    {
        var len = _r.GetInt();
        var a = new float[len];
        for (var i = 0; i < len; i++) a[i] = _r.GetFloat();
        return a;
    }
    public string[] GetStringArray()
    {
        var len = _r.GetInt();
        var a = new string[len];
        for (var i = 0; i < len; i++) a[i] = _r.GetString();
        return a;
    }
}
}
