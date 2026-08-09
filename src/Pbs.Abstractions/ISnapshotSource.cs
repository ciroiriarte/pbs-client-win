namespace Pbs.Abstractions;

/// <summary>
/// Abstracts where a backup reads its files from: the live filesystem, or a point-in-time snapshot
/// (VSS on Windows). The backup path always calls <see cref="Map"/> to translate a live path to the
/// path it should actually read, so the same orchestration works with or without snapshots.
/// </summary>
public interface ISnapshotSource : IDisposable
{
    /// <summary>Translate a live path to the path to read (identity, or its shadow-copy path).</summary>
    string Map(string livePath);

    /// <summary>
    /// Open a whole volume (e.g. <c>C:</c>) as a raw image stream plus its size in bytes — used for
    /// full-disk image backup. Only meaningful for a real snapshot source (VSS); the identity source
    /// does not support it.
    /// </summary>
    (Stream Stream, long Size) OpenImage(string volume);

    /// <summary>Signal that reading finished successfully (lets writers finalize, e.g. truncate logs).</summary>
    void Complete();
}
