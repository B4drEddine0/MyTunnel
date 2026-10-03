namespace MyTunnel.Contracts;

/// <summary>Constants shared by the gateway and the CLI. Changing a limit is a protocol change.</summary>
public static class TunnelProtocol
{
    /// <summary>Version spoken by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Oldest peer version this build will still talk to.</summary>
    public const int MinSupportedVersion = 1;

    public const string TunnelPath = "/tunnel";

    /// <summary>Upper bound for a single JSON control message (after reassembling fragments).</summary>
    public const int MaxControlMessageBytes = 64 * 1024;

    /// <summary>Upper bound for the payload of one binary body frame.</summary>
    public const int MaxBodyChunkBytes = 32 * 1024;

    /// <summary>Bytes a sender may have in flight per body stream before it needs a window update.</summary>
    public const int InitialWindowBytes = 256 * 1024;

    public const int MaxHeaderCount = 128;

    /// <summary>Total of all header names and values in one request or response.</summary>
    public const int MaxHeaderBytes = 32 * 1024;

    public const int MaxTargetLength = 8 * 1024;
}
