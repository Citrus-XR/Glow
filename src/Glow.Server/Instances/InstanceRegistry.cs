namespace Glow.Server.Instances;

// Registry of active instances keyed by name. Instances are created on
// demand via TryCreate and swept by CleanupExpired once they have sat
// empty past their configured EmptyInstanceTtlMs, releasing the name so
// the next join allocates a fresh Instance with NextPeerId = 1.
public sealed class InstanceRegistry
{
    readonly Dictionary<string, Instance> _instances = [];

    public IReadOnlyDictionary<string, Instance> All => _instances;
    public int Count => _instances.Count;

    public bool TryCreate(string name, out Instance instance)
    {
        if (_instances.ContainsKey(name))
        {
            instance = null!;
            return false;
        }
        instance = new Instance(name);
        _instances[name] = instance;
        return true;
    }

    public bool TryGet(string name, out Instance instance)
    {
        if (_instances.TryGetValue(name, out var found))
        {
            instance = found;
            return true;
        }
        instance = null!;
        return false;
    }

    public bool Remove(string name) => _instances.Remove(name);

    // Sweeps instances whose empty mark has aged past EmptyInstanceTtlMs.
    // Returns the removed names so the caller can log without holding the
    // enumerator open while mutating the underlying dictionary.
    //
    // Semantics of EmptyInstanceTtlMs:
    //   == 0  → destroy on the first sweep tick after ActivePeerCount reaches 0 (default, "immediate")
    //   >  0  → destroy after that many ms of continuous emptiness
    //   <  0  → never destroy (opt out; instance persists across empty periods)
    public List<string> CleanupExpired(long nowMs)
    {
        List<string>? expired = null;
        foreach (var (name, instance) in _instances)
        {
            if (instance.EmptyInstanceTtlMs < 0) continue;
            if (instance.ActivePeerCount != 0) continue;
            if (instance.EmptyAtMs is not long emptyAt) continue;
            if (nowMs - emptyAt < instance.EmptyInstanceTtlMs) continue;
            (expired ??= []).Add(name);
        }
        if (expired is null) return [];
        foreach (var name in expired) _instances.Remove(name);
        return expired;
    }
}
