namespace Glow.Server;

public sealed record ServerOptions
{
    public int Port { get; init; } = 1840;
    public string ConnectKey { get; init; } = "glow";
    public string? DefaultInstanceName { get; init; } = "default";
    public int ServerTimeBroadcastIntervalMs { get; init; } = 1000;
    public string? StatusHttpPrefix { get; init; } = "http://localhost:5155/";
    public int PerSessionBytesPerSecond { get; init; } = 11 * 1024;
    public string PeerDataDirectory { get; init; } = "peer-data";

    // Byte quota enforced on every (user, store-tag) pair in the
    // PeerData store. A SetPeerData patch that would push a tag over
    // this size is dropped whole and the caller sees QuotaExceeded. Set
    // as low as your game needs -- the default fits either a modest
    // properties dict or a compact serialized state blob per tag.
    public int PeerDataStoreQuotaBytes { get; init; } = Persistence.PeerDataStore.DefaultPerStoreQuotaBytes;

    // Number of LiteNetLib channels advertised on the socket. Application
    // sends pick a channel byte in [0, ChannelsCount). Same value must
    // be used on both server and client sides.
    public byte ChannelsCount { get; init; } = 16;

    // Tick interval for LiteNetLib's internal logic thread (ms). Lower =
    // faster ACKs / less latency, higher CPU idle cost. Default 5 ms is
    // a good balance for real-time worlds; the library default is 15.
    public int TransportUpdateIntervalMs { get; init; } = 5;

    // How long an instance may stay empty (zero active peers) before the
    // registry destroys it. Next join with the same name allocates a
    // fresh Instance whose NextPeerId restarts at 1 -- destroying the
    // instance is how Glow rotates the peer-id namespace so old ids
    // don't collide with new arrivals.
    //   0  → immediate destroy on the first sweep after emptiness (default)
    //   >0 → wait that many ms before destroying
    //   <0 → never destroy (instance persists across empty periods)
    public int EmptyInstanceTtlMs { get; init; } = 0;

    // When true (default), per-event logs are printed to stdout: peer
    // connect/disconnect, instance join/leave, object ownership changes,
    // and empty-instance sweeps. Set to false to run silently — startup
    // banners, shutdown notices, and errors are still printed regardless.
    public bool Verbose { get; init; } = true;
}
