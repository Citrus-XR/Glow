using System.Runtime.CompilerServices;
using Glow.Shared.Protocol;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Glow.Shared
{
// Tagged union for peer / instance property values. Struct-based so
// primitives (bool/int/long/float/double) live inline without boxing;
// only String and Bytes touch the reference field. Equality is
// structural (bytewise for Bytes, ordinal for String) so CAS on
// SetProperty is straightforward.
public readonly struct PropertyValue : IEquatable<PropertyValue>
{
    // For primitives we stash the raw bits into _bits. For string/bytes
    // _ref holds the reference and _bits is unused.
    readonly long _bits;
    readonly object? _ref;

    public PropertyKind Kind { get; }

    PropertyValue(PropertyKind kind, long bits, object? refValue)
    {
        Kind = kind;
        _bits = bits;
        _ref = refValue;
    }

    // ---- Factories ------------------------------------------------

    public static readonly PropertyValue Null = new(PropertyKind.Null, 0, null);

    public static PropertyValue From(bool v) => new(PropertyKind.Bool, v ? 1 : 0, null);
    public static PropertyValue From(int v) => new(PropertyKind.Int, v, null);
    public static PropertyValue From(long v) => new(PropertyKind.Long, v, null);
    public static PropertyValue From(float v) => new(PropertyKind.Float, BitConverter.SingleToInt32Bits(v), null);
    public static PropertyValue From(double v) => new(PropertyKind.Double, BitConverter.DoubleToInt64Bits(v), null);
    public static PropertyValue From(string v) => new(PropertyKind.String, 0, v);
    public static PropertyValue From(byte[] v) => new(PropertyKind.Bytes, 0, v);

    // ---- Accessors -----------------------------------------------

    public bool IsNull => Kind == PropertyKind.Null;

    public bool AsBool => Kind == PropertyKind.Bool ? _bits != 0 : throw KindMismatch(PropertyKind.Bool);
    public int AsInt => Kind == PropertyKind.Int ? (int)_bits : throw KindMismatch(PropertyKind.Int);
    public long AsLong => Kind == PropertyKind.Long ? _bits : throw KindMismatch(PropertyKind.Long);
    public float AsFloat => Kind == PropertyKind.Float ? BitConverter.Int32BitsToSingle((int)_bits) : throw KindMismatch(PropertyKind.Float);
    public double AsDouble => Kind == PropertyKind.Double ? BitConverter.Int64BitsToDouble(_bits) : throw KindMismatch(PropertyKind.Double);
    public string AsString => Kind == PropertyKind.String ? (string)_ref! : throw KindMismatch(PropertyKind.String);
    public byte[] AsBytes => Kind == PropertyKind.Bytes ? (byte[])_ref! : throw KindMismatch(PropertyKind.Bytes);

    Exception KindMismatch(PropertyKind expected) =>
        new InvalidOperationException($"PropertyValue is {Kind}, not {expected}");

    // ---- Equality -------------------------------------------------

    public bool Equals(PropertyValue other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            PropertyKind.Null => true,
            PropertyKind.String => string.Equals((string?)_ref, (string?)other._ref, StringComparison.Ordinal),
            PropertyKind.Bytes => BytesEqual((byte[]?)_ref, (byte[]?)other._ref),
            _ => _bits == other._bits,
        };
    }

    static bool BytesEqual(byte[]? a, byte[]? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => ((ReadOnlySpan<byte>)a).SequenceEqual(b),
        };

    public override bool Equals(object? obj) => obj is PropertyValue pv && Equals(pv);

    public override int GetHashCode()
    {
        var h = HashCode.Combine((byte)Kind, _bits);
        return Kind switch
        {
            PropertyKind.String => HashCode.Combine(h, (string?)_ref),
            PropertyKind.Bytes => HashCode.Combine(h, BytesHash((byte[]?)_ref)),
            _ => h,
        };
    }

    static int BytesHash(byte[]? b)
    {
        if (b is null) return 0;
        var h = new HashCode();
        for (var i = 0; i < b.Length; i++) h.Add(b[i]);
        return h.ToHashCode();
    }

    public static bool operator ==(PropertyValue a, PropertyValue b) => a.Equals(b);
    public static bool operator !=(PropertyValue a, PropertyValue b) => !a.Equals(b);

    static readonly char[] HexDigits = "0123456789ABCDEF".ToCharArray();
    static string BytesToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = HexDigits[bytes[i] >> 4];
            chars[i * 2 + 1] = HexDigits[bytes[i] & 0x0F];
        }
        return new string(chars);
    }

    public override string ToString() => Kind switch
    {
        PropertyKind.Null => "null",
        PropertyKind.Bool => AsBool ? "true" : "false",
        PropertyKind.Int => AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.Long => AsLong.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.Float => AsFloat.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.Double => AsDouble.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.String => $"\"{AsString}\"",
        PropertyKind.Bytes => $"0x{BytesToHex(AsBytes)}",
        _ => $"?({Kind})",
    };
}
}
