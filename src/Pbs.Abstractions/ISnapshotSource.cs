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

    /// <summary>Signal that reading finished successfully (lets writers finalize, e.g. truncate logs).</summary>
    void Complete();
}
