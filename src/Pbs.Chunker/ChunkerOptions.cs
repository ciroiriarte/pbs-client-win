namespace Pbs.Chunker;

/// <summary>
/// Size bounds for content-defined chunking. Defaults mirror Proxmox Backup Server's dynamic
/// chunker target of a ~4 MiB average chunk, bounded to a quarter/four-times window.
/// </summary>
/// <remarks>
/// Exact boundary parity with PBS (its specific rolling-hash constants) is a separate interop
/// task; these bounds and the Gear rolling hash produce valid, stable, deduplicating chunk
/// streams that a PBS datastore stores and restores correctly.
/// </remarks>
public sealed record ChunkerOptions
{
    /// <summary>Minimum chunk size in bytes. Boundaries are suppressed below this.</summary>
    public int MinSize { get; init; } = 1024 * 1024;          // 1 MiB

    /// <summary>Maximum chunk size in bytes. A boundary is forced at this length.</summary>
    public int MaxSize { get; init; } = 16 * 1024 * 1024;     // 16 MiB

    /// <summary>
    /// Number of low bits that must be zero in the rolling hash to cut a boundary. The average
    /// chunk size is approximately 2^<see cref="MaskBits"/> bytes (default ~4 MiB).
    /// </summary>
    public int MaskBits { get; init; } = 22;                  // ~4 MiB average

    /// <summary>PBS default fixed-chunk size for image (<c>.fidx</c>) archives.</summary>
    public const int FixedChunkSize = 4 * 1024 * 1024;        // 4 MiB

    public void Validate()
    {
        if (MinSize <= 0) throw new ArgumentOutOfRangeException(nameof(MinSize));
        if (MaxSize < MinSize) throw new ArgumentOutOfRangeException(nameof(MaxSize), "MaxSize must be >= MinSize.");
        if (MaskBits is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(MaskBits));
    }
}
