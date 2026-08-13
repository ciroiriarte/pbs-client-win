namespace Pbs.Pxar;

/// <summary>
/// A read-only mount engine that projects a pxar archive's contents at a mount target (a directory,
/// or — for engines that support it — a drive letter). Implementations are backed by a shared
/// <see cref="PxarMountModel"/>: the catalog supplies enumeration and metadata, the index supplies
/// payload offsets, and a seekable content stream serves file bytes on demand (ranged reads, no
/// full restore). This is the abstraction behind the dual-engine <c>mount</c> command — ProjFS on
/// Windows client / Server-with-Desktop-Experience, WinFsp on Server Core (see issue #29).
/// </summary>
public interface IPbsMountProvider : IDisposable
{
    /// <summary>Stable engine id, e.g. <c>projfs</c> or <c>winfsp</c>.</summary>
    string Id { get; }

    /// <summary>
    /// Begin serving the mount. Blocks only long enough to establish the virtualization/filesystem;
    /// file content is fetched lazily as it is read. Throws if the engine fails to start.
    /// </summary>
    void Start();

    /// <summary>
    /// Enter teardown. Engines that hydrate on-disk placeholders (ProjFS) use this to permit deletes
    /// so the owning process can clear those placeholders before <see cref="IDisposable.Dispose"/>
    /// stops the engine. Engines that own their namespace entirely (WinFsp) treat this as a no-op.
    /// </summary>
    void PrepareForTeardown();
}
