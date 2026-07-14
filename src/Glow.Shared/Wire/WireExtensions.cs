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
// Writer/reader helpers keeping the top-level codecs tidy. Nothing here
// depends on reflection; everything is direct field IO so NativeAOT is
// happy and the hot path stays boxing-free.
public static class WireExtensions
{
    // ---- PropertyValue --------------------------------------------

    public static void PutProperty(this NetDataWriter w, in PropertyValue v)
    {
        w.Put((byte)v.Kind);
        switch (v.Kind)
        {
            case PropertyKind.Null: break;
            case PropertyKind.Bool: w.Put(v.AsBool); break;
            case PropertyKind.Int: w.Put(v.AsInt); break;
            case PropertyKind.Long: w.Put(v.AsLong); break;
            case PropertyKind.Float: w.Put(v.AsFloat); break;
            case PropertyKind.Double: w.Put(v.AsDouble); break;
            case PropertyKind.String: w.Put(v.AsString); break;
            case PropertyKind.Bytes: w.PutBytesWithLength(v.AsBytes); break;
            default: throw new InvalidOperationException($"Unknown PropertyKind {v.Kind}");
        }
    }

    public static PropertyValue GetProperty(this NetDataReader r)
    {
        var kind = (PropertyKind)r.GetByte();
        return kind switch
        {
            PropertyKind.Null => PropertyValue.Null,
            PropertyKind.Bool => PropertyValue.From(r.GetBool()),
            PropertyKind.Int => PropertyValue.From(r.GetInt()),
            PropertyKind.Long => PropertyValue.From(r.GetLong()),
            PropertyKind.Float => PropertyValue.From(r.GetFloat()),
            PropertyKind.Double => PropertyValue.From(r.GetDouble()),
            PropertyKind.String => PropertyValue.From(r.GetString()),
            PropertyKind.Bytes => PropertyValue.From(r.GetBytesWithLength()),
            _ => throw new InvalidOperationException($"Unknown PropertyKind {(byte)kind}"),
        };
    }

    // ---- Dictionary<string, PropertyValue> ------------------------

    public static void PutPropertyMap(this NetDataWriter w, Dictionary<string, PropertyValue>? map)
    {
        if (map is null)
        {
            w.Put((ushort)0);
            return;
        }
        if (map.Count > ushort.MaxValue)
            throw new InvalidOperationException("PropertyMap exceeds 65535 entries.");
        w.Put((ushort)map.Count);
        foreach (var kv in map)
        {
            w.Put(kv.Key);
            w.PutProperty(kv.Value);
        }
    }

    public static Dictionary<string, PropertyValue> GetPropertyMap(this NetDataReader r)
    {
        var count = r.GetUShort();
        var map = new Dictionary<string, PropertyValue>(count);
        for (var i = 0; i < count; i++)
        {
            var key = r.GetString();
            var value = r.GetProperty();
            map[key] = value;
        }
        return map;
    }

    // ---- Dictionary<byte store, Dictionary<string, PropertyValue>> ---
    // Snapshot messages (HelloAck / PeerJoined / GetPeerDataAck) carry
    // every populated PeerData store at once. The outer byte is a
    // client-chosen namespace tag; the meaning of each tag is defined
    // by the application, not by Glow.
    public static void PutStorePropertyMap(this NetDataWriter w,
        Dictionary<byte, Dictionary<string, PropertyValue>>? stores)
    {
        if (stores is null)
        {
            w.Put((byte)0);
            return;
        }
        if (stores.Count > byte.MaxValue)
            throw new InvalidOperationException("StorePropertyMap exceeds 255 stores.");
        w.Put((byte)stores.Count);
        foreach (var kv in stores)
        {
            w.Put(kv.Key);
            w.PutPropertyMap(kv.Value);
        }
    }

    public static Dictionary<byte, Dictionary<string, PropertyValue>> GetStorePropertyMap(this NetDataReader r)
    {
        var count = r.GetByte();
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>(count);
        for (var i = 0; i < count; i++)
        {
            var store = r.GetByte();
            var map = r.GetPropertyMap();
            stores[store] = map;
        }
        return stores;
    }

    // ---- Optional string (bool-prefixed) --------------------------

    public static void PutOptString(this NetDataWriter w, string? s)
    {
        if (s is null) w.Put(false);
        else { w.Put(true); w.Put(s); }
    }

    public static string? GetOptString(this NetDataReader r) =>
        r.GetBool() ? r.GetString() : null;

    // ---- Optional int[] -------------------------------------------

    public static void PutOptIntArray(this NetDataWriter w, int[]? a)
    {
        if (a is null) w.Put((ushort)0xFFFF);
        else
        {
            if (a.Length > ushort.MaxValue - 1)
                throw new InvalidOperationException("int[] exceeds 65534 entries.");
            w.Put((ushort)a.Length);
            foreach (var v in a) w.Put(v);
        }
    }

    public static int[]? GetOptIntArray(this NetDataReader r)
    {
        var len = r.GetUShort();
        if (len == 0xFFFF) return null;
        var a = new int[len];
        for (var i = 0; i < len; i++) a[i] = r.GetInt();
        return a;
    }

    // ---- Payload (opaque byte slice) ------------------------------

    public static void PutPayload(this NetDataWriter w, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new InvalidOperationException("Payload exceeds 65535 bytes.");
        w.Put((ushort)payload.Length);
        w.Put(payload.Span);
    }

    public static byte[] GetPayload(this NetDataReader r)
    {
        var len = r.GetUShort();
        var buf = new byte[len];
        r.GetBytes(buf, len);
        return buf;
    }

    // ---- Dictionary<int, int> (object owners) --------------------

    public static void PutIntIntMap(this NetDataWriter w, Dictionary<int, int>? map)
    {
        if (map is null || map.Count == 0)
        {
            w.Put((ushort)0);
            return;
        }
        if (map.Count > ushort.MaxValue)
            throw new InvalidOperationException("IntIntMap exceeds 65535 entries.");
        w.Put((ushort)map.Count);
        foreach (var kv in map)
        {
            w.Put(kv.Key);
            w.Put(kv.Value);
        }
    }

    public static Dictionary<int, int> GetIntIntMap(this NetDataReader r)
    {
        var count = r.GetUShort();
        var map = new Dictionary<int, int>(count);
        for (var i = 0; i < count; i++)
        {
            var k = r.GetInt();
            var v = r.GetInt();
            map[k] = v;
        }
        return map;
    }
}
}
