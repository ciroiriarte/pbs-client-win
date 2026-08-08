namespace Pbs.Abstractions;

/// <summary>
/// One logical archive a provider contributes to a backup snapshot (e.g. a filesystem tree as a
/// <c>root.pxar</c>, a volume image as <c>disk.img.fidx</c>, or a SQL dump as a stream).
/// </summary>
/// <param name="Name">Archive name within the snapshot, e.g. <c>root.pxar</c> or <c>disk0.img</c>.</param>
/// <param name="IndexType">Whether the archive is chunked dynamically or at a fixed size.</param>
/// <param name="Content">The plaintext byte stream to be chunked, compressed, and uploaded.</param>
public sealed record BackupArchive(string Name, IndexType IndexType, Stream Content);

/// <summary>
/// Produces one or more <see cref="BackupArchive"/> streams for a snapshot. Implementations
/// encapsulate <em>how</em> a source is made consistent (VSS snapshot, SQL VDI, plain walk) and
/// expose the resulting bytes uniformly to the transport/dedup core.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="QuiesceAsync"/> (optional app freeze) → <see cref="OpenArchivesAsync"/>
/// (consumer reads and uploads) → <see cref="CompleteAsync"/> (post-backup: release snapshot,
/// truncate logs). Implementations must call <see cref="CompleteAsync"/> semantics even on
/// failure via <see cref="IAsyncDisposable"/>.
/// </remarks>
public interface IBackupProvider : IAsyncDisposable
{
    /// <summary>Stable identifier for the provider, e.g. <c>filesystem</c>, <c>image</c>, <c>mssql</c>.</summary>
    string Id { get; }

    /// <summary>Prepare a consistent point-in-time view of the source (e.g. take a VSS snapshot).</summary>
    Task QuiesceAsync(CancellationToken ct = default);

    /// <summary>Enumerate the archives that make up this snapshot. Streams are read exactly once.</summary>
    IAsyncEnumerable<BackupArchive> OpenArchivesAsync(CancellationToken ct = default);

    /// <summary>Signal successful upload so the source may release the snapshot / truncate logs.</summary>
    Task CompleteAsync(bool success, CancellationToken ct = default);
}
