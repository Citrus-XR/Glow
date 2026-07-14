using System.Globalization;
using System.Text;
using System.Text.Json;
using Glow.Shared;
using Glow.Shared.Protocol;

namespace Glow.Server.Persistence;

// Serializes the byte-tagged store map to human-readable JSON.
// Top-level shape:
//   { "<tag>": { "<key>": <value>, ... }, ... }
// where <tag> is the store's byte value rendered as a decimal integer
// string ("0", "1", "42", ...). Empty tags are not emitted.
//
// Value encoding: primitives map to JSON primitives, byte[] wraps in
// { "$b": "<base64>" }. Numeric variants widen to Long/Double on disk
// (JSON has no distinct kinds); the wire codec still supports the
// narrower types on the network path.
public static class PeerDataJsonCodec
{
    public static byte[] Encode(Dictionary<byte, Dictionary<string, PropertyValue>> stores)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            // Sort by tag so the on-disk file is deterministic across runs
            // -- helps diffing hand-edited state files.
            foreach (var kv in stores.OrderBy(kv => kv.Key))
            {
                if (kv.Value.Count == 0) continue;
                WriteSubstore(w, kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value);
            }
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static Dictionary<byte, Dictionary<string, PropertyValue>> Decode(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new FormatException("PeerData JSON must start with an object.");
        var stores = new Dictionary<byte, Dictionary<string, PropertyValue>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return stores;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new FormatException($"Expected property name, got {reader.TokenType}");
            var rawKey = reader.GetString()!;
            if (!reader.Read()) throw new FormatException("Missing substore value.");
            // Non-byte top-level keys are silently skipped so a stray
            // hand-added section doesn't wedge the whole file.
            if (!byte.TryParse(rawKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tag))
            {
                reader.Skip();
                continue;
            }
            var sub = new Dictionary<string, PropertyValue>();
            ReadIntoSubstore(ref reader, sub);
            if (sub.Count > 0) stores[tag] = sub;
        }
        throw new FormatException("Unexpected end of JSON while reading PeerData.");
    }

    static void WriteSubstore(Utf8JsonWriter w, string key, Dictionary<string, PropertyValue> substore)
    {
        w.WritePropertyName(key);
        w.WriteStartObject();
        foreach (var kv in substore)
        {
            w.WritePropertyName(kv.Key);
            WriteValue(w, kv.Value);
        }
        w.WriteEndObject();
    }

    static void ReadIntoSubstore(ref Utf8JsonReader r, Dictionary<string, PropertyValue> target)
    {
        if (r.TokenType != JsonTokenType.StartObject)
            throw new FormatException($"Substore must be an object, got {r.TokenType}");
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject) return;
            if (r.TokenType != JsonTokenType.PropertyName)
                throw new FormatException($"Expected property name inside substore, got {r.TokenType}");
            var key = r.GetString()!;
            if (!r.Read()) throw new FormatException("Missing property value inside substore.");
            target[key] = ReadValue(ref r);
        }
        throw new FormatException("Unexpected end of JSON while reading substore.");
    }

    static void WriteValue(Utf8JsonWriter w, in PropertyValue v)
    {
        switch (v.Kind)
        {
            case PropertyKind.Null: w.WriteNullValue(); break;
            case PropertyKind.Bool: w.WriteBooleanValue(v.AsBool); break;
            case PropertyKind.Int: w.WriteNumberValue(v.AsInt); break;
            case PropertyKind.Long: w.WriteNumberValue(v.AsLong); break;
            case PropertyKind.Float: w.WriteNumberValue(v.AsFloat); break;
            case PropertyKind.Double: w.WriteNumberValue(v.AsDouble); break;
            case PropertyKind.String: w.WriteStringValue(v.AsString); break;
            case PropertyKind.Bytes:
                w.WriteStartObject();
                w.WriteBase64String("$b", v.AsBytes);
                w.WriteEndObject();
                break;
            default: throw new NotSupportedException($"Unknown PropertyKind: {v.Kind}");
        }
    }

    static PropertyValue ReadValue(ref Utf8JsonReader r)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Null: return PropertyValue.Null;
            case JsonTokenType.True: return PropertyValue.From(true);
            case JsonTokenType.False: return PropertyValue.From(false);
            case JsonTokenType.String: return PropertyValue.From(r.GetString()!);
            case JsonTokenType.Number:
                if (r.TryGetInt64(out var l)) return PropertyValue.From(l);
                return PropertyValue.From(r.GetDouble());
            case JsonTokenType.StartObject:
                // Only wrapper we understand: { "$b": "<base64>" }
                if (!r.Read() || r.TokenType != JsonTokenType.PropertyName || r.GetString() != "$b")
                    throw new FormatException("Unknown object shape in PeerData JSON (expected \"$b\").");
                if (!r.Read() || r.TokenType != JsonTokenType.String)
                    throw new FormatException("Expected base64 string after \"$b\".");
                var bytes = r.GetBytesFromBase64();
                if (!r.Read() || r.TokenType != JsonTokenType.EndObject)
                    throw new FormatException("Expected end of $b object.");
                return PropertyValue.From(bytes);
            default:
                throw new FormatException($"Unexpected token {r.TokenType} in PeerData JSON.");
        }
    }

    public static string EncodeString(Dictionary<byte, Dictionary<string, PropertyValue>> stores) =>
        Encoding.UTF8.GetString(Encode(stores));

    public static Dictionary<byte, Dictionary<string, PropertyValue>> DecodeString(string json) =>
        Decode(Encoding.UTF8.GetBytes(json));
}
