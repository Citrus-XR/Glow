using Glow.Shared;

namespace Glow.Server.Persistence;

// Per-user PeerData store organized as byte-tagged namespaces. Each user
// owns a Dictionary<byte, Dictionary<string, PropertyValue>>: the outer
// byte is a client-chosen "store tag" (0..255), the inner map is the
// property set for that namespace. Client apps decide what each tag
// means. Slash-delimited keys receive an independent quota per first
// two segments; unscoped keys share one quota bucket. Over-quota writes
// are dropped whole.
//
// On disk each user maps to `<baseDir>/<userId>.json`. Top-level keys
// are the numeric byte tag as a string ("0", "1", "42", ...), values are
// property objects. Load is lazy: the first Load reads from disk if a
// file exists, otherwise returns an empty dict. Filesystem-unsafe
// UserIds are sanitized (non-alphanumeric collapses to _). Save is
// synchronous on every mutation so a crash mid-write loses at most one
// merge.
public sealed class PeerDataStore
{
    // Default byte cap per (user, store tag, key namespace). Chosen to comfortably hold
    // a small properties dict or a mid-size serialized blob without
    // encouraging clients to treat the store like a general filesystem.
    // Override via ServerOptions.PeerDataStoreQuotaBytes.
    public const int DefaultPerStoreQuotaBytes = 100 * 1024;

    readonly string _baseDirectory;
    readonly int _perStoreQuotaBytes;
    readonly Dictionary<string, Dictionary<byte, Dictionary<string, PropertyValue>>> _cache = [];

    public PeerDataStore(string baseDirectory, int perStoreQuotaBytes = DefaultPerStoreQuotaBytes)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory);
        _perStoreQuotaBytes = perStoreQuotaBytes;
        Directory.CreateDirectory(_baseDirectory);
    }

    public string BaseDirectory => _baseDirectory;
    public int PerStoreQuotaBytes => _perStoreQuotaBytes;

    // Full snapshot for a user (all tags currently populated on disk /
    // in memory). Returns a fresh copy at every level so mutations by
    // callers don't leak back into the cache.
    public Dictionary<byte, Dictionary<string, PropertyValue>> LoadAll(string userId)
    {
        var per = LoadInternal(userId);
        var copy = new Dictionary<byte, Dictionary<string, PropertyValue>>(per.Count);
        foreach (var kv in per)
            copy[kv.Key] = new Dictionary<string, PropertyValue>(kv.Value);
        return copy;
    }

    // Single-tag snapshot. Missing tag returns an empty dict.
    public Dictionary<string, PropertyValue> Load(string userId, byte store)
    {
        var per = LoadInternal(userId);
        return per.TryGetValue(store, out var sub)
            ? new Dictionary<string, PropertyValue>(sub)
            : new Dictionary<string, PropertyValue>();
    }

    // Merges patch into the requested tag (null values delete). Returns
    // (true, snapshot) on success or (false, snapshot) if the patch would
    // push any namespace over PerStoreQuotaBytes -- in that case nothing is
    // written and the returned snapshot is the pre-merge state. The
    // caller responds with QuotaExceeded to the client.
    public (bool Ok, Dictionary<string, PropertyValue> Snapshot) Merge(
        string userId, byte store, Dictionary<string, PropertyValue> patch)
    {
        var per = LoadInternal(userId);
        if (!per.TryGetValue(store, out var current))
        {
            current = new Dictionary<string, PropertyValue>();
            per[store] = current;
        }

        // Build a projected copy so we can size-check before committing.
        // Cheap for the typical 10-100 key working set; the alternative
        // (in-place merge then rollback on quota fail) is riskier.
        var projected = new Dictionary<string, PropertyValue>(current);
        foreach (var kv in patch)
        {
            if (kv.Value.IsNull) projected.Remove(kv.Key);
            else projected[kv.Key] = kv.Value;
        }

        if (EstimateQuotaScopes(projected).Values.Any(size => size > _perStoreQuotaBytes))
        {
            return (false, new Dictionary<string, PropertyValue>(current));
        }

        // Commit in place and persist. Errors on disk write bubble up as
        // stderr but don't roll back the in-memory state -- the next
        // successful merge will re-serialize the whole file.
        current.Clear();
        foreach (var kv in projected) current[kv.Key] = kv.Value;

        // Empty tags don't need to occupy a JSON entry. Prune so a
        // delete-everything patch doesn't leave a "0: {}" stub behind.
        if (current.Count == 0) per.Remove(store);

        try
        {
            var bytes = PeerDataJsonCodec.Encode(per);
            File.WriteAllBytes(PathFor(userId), bytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[persistence] failed to save {userId}: {ex.Message}");
        }

        return (true, new Dictionary<string, PropertyValue>(current));
    }

    // Approximate byte cost of a store's property set. Charges key
    // length (UTF-8 upper bound) plus a fixed per-value overhead based
    // on kind; Bytes values charge their real payload length. Not an
    // exact JSON size, but stable and monotonic enough to drive the
    // per-tag cap.
    public static int EstimateStoreSize(Dictionary<string, PropertyValue> store)
    {
        var total = 0;
        foreach (var kv in store)
        {
            total += System.Text.Encoding.UTF8.GetByteCount(kv.Key);
            total += EstimateValueSize(kv.Value);
        }
        return total;
    }

    public static Dictionary<string, int> EstimateQuotaScopes(
        Dictionary<string, PropertyValue> store)
    {
        var scopes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in store)
        {
            var scope = QuotaScope(kv.Key);
            var size = System.Text.Encoding.UTF8.GetByteCount(kv.Key) + EstimateValueSize(kv.Value);
            scopes[scope] = scopes.TryGetValue(scope, out var current) ? current + size : size;
        }
        return scopes;
    }

    static string QuotaScope(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var first = key.IndexOf('/');
        if (first <= 0) return string.Empty;
        var second = key.IndexOf('/', first + 1);
        return second > first + 1 ? key[..second] : string.Empty;
    }

    static int EstimateValueSize(in PropertyValue v) => v.Kind switch
    {
        Shared.Protocol.PropertyKind.Null => 1,
        Shared.Protocol.PropertyKind.Bool => 1,
        Shared.Protocol.PropertyKind.Int => 4,
        Shared.Protocol.PropertyKind.Long => 8,
        Shared.Protocol.PropertyKind.Float => 4,
        Shared.Protocol.PropertyKind.Double => 8,
        Shared.Protocol.PropertyKind.String => System.Text.Encoding.UTF8.GetByteCount(v.AsString),
        Shared.Protocol.PropertyKind.Bytes => v.AsBytes.Length,
        _ => 0,
    };

    Dictionary<byte, Dictionary<string, PropertyValue>> LoadInternal(string userId)
    {
        if (_cache.TryGetValue(userId, out var cached)) return cached;
        var path = PathFor(userId);
        if (File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var loaded = PeerDataJsonCodec.Decode(bytes);
                _cache[userId] = loaded;
                return loaded;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[persistence] failed to load {path}: {ex.Message}");
            }
        }
        var fresh = new Dictionary<byte, Dictionary<string, PropertyValue>>();
        _cache[userId] = fresh;
        return fresh;
    }

    string PathFor(string userId)
    {
        var safe = new string(userId.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(_baseDirectory, safe + ".json");
    }
}
