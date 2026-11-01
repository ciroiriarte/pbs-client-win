namespace Pbs.Pxar;

/// <summary>
/// The engine-agnostic backing store for a read-only pxar mount, shared by every
/// <see cref="IPbsMountProvider"/> (ProjFS, WinFsp). It combines the three inputs a mount needs and
/// nothing engine-specific:
/// <list type="bullet">
///   <item>the parsed <b>catalog</b> tree — directory structure, names, sizes, mtimes (enumeration + metadata);</item>
///   <item>the <b>index</b> (<see cref="PxarIndexer"/> output) — payload offset/length per file;</item>
///   <item>a readable, seekable <b>content</b> stream — typically a ReaderSession ranged-read stream, so file
///         bytes are fetched on demand rather than restored up front.</item>
/// </list>
/// Reads are serialized on the content stream via an internal lock, so a single model safely serves
/// concurrent callbacks from either engine. This type has no Windows dependencies and is unit-tested
/// on all platforms; the engine providers are thin adapters over it.
/// </summary>
public sealed class PxarMountModel
{
    private readonly Stream _content;
    private readonly Stream? _payloadContent;
    private readonly object _contentLock = new();
    private readonly Dictionary<string, PxarCatalogEntry> _catalogByPath;
    private readonly Dictionary<string, PxarEntryLocation> _locations;

    /// <summary>The catalog root (the mounted archive's top directory).</summary>
    public PxarCatalogEntry Root { get; }

    /// <param name="catalog">Parsed catalog for the mounted archive (its root directory and children).</param>
    /// <param name="pxarContent">Readable, seekable pxar content stream (payloads live at the indexed offsets).</param>
    /// <param name="index">Payload locations from <see cref="PxarIndexer.BuildIndexAsync"/>.</param>
    /// <param name="payloadContent">For a split archive (issue #33): readable, seekable payload stream
    /// (<c>.ppxar</c>) that locations with <see cref="PxarEntryLocation.InPayloadArchive"/> read from.</param>
    public PxarMountModel(PxarCatalogEntry catalog, Stream pxarContent, IReadOnlyList<PxarEntryLocation> index, Stream? payloadContent = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pxarContent);
        ArgumentNullException.ThrowIfNull(index);

        if (!pxarContent.CanRead || !pxarContent.CanSeek)
            throw new ArgumentException("a pxar mount requires a readable, seekable content stream", nameof(pxarContent));
        if (payloadContent is not null && (!payloadContent.CanRead || !payloadContent.CanSeek))
            throw new ArgumentException("a split-archive mount requires a readable, seekable payload stream", nameof(payloadContent));
        if (payloadContent is null && index.Any(e => e.InPayloadArchive))
            throw new ArgumentException("the index references a payload stream (split archive), but none was given", nameof(payloadContent));
        _payloadContent = payloadContent;

        Root = catalog;
        _content = pxarContent;
        _catalogByPath = BuildCatalogMap(catalog);
        _locations = index.ToDictionary(entry => NormalizePath(entry.RelativePath), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Look up a catalog entry (directory, file, symlink, …) by relative path; <c>""</c> is the root.</summary>
    public bool TryGetEntry(string? relativePath, out PxarCatalogEntry entry) =>
        _catalogByPath.TryGetValue(NormalizePath(relativePath), out entry!);

    /// <summary>
    /// Look up the payload location of a <b>file</b> by relative path. Returns false for directories,
    /// symlinks, special entries (no local payload), or unknown paths.
    /// </summary>
    public bool TryGetFileLocation(string? relativePath, out PxarEntryLocation location)
    {
        if (_locations.TryGetValue(NormalizePath(relativePath), out location!) && location.Kind == PxarEntryKind.File)
            return true;

        location = null!;
        return false;
    }

    /// <summary>
    /// Read a file's payload into <paramref name="buffer"/>. Reads at most <paramref name="count"/> bytes
    /// starting <paramref name="byteOffset"/> into the file, clamped to the file's payload length, under
    /// the content lock. Returns the number of bytes read (0 when <paramref name="byteOffset"/> is at or
    /// past end-of-file). Throws <see cref="EndOfStreamException"/> if the content stream ends before the
    /// clamped range is satisfied (a truncated/corrupt archive).
    /// </summary>
    public int ReadFile(PxarEntryLocation location, long byteOffset, byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(buffer);
        if (byteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        if (location.Kind != PxarEntryKind.File)
            throw new ArgumentException("location is not a file payload", nameof(location));
        if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));

        if (byteOffset >= location.PayloadLength || count == 0)
            return 0;

        var toRead = (int)Math.Min(count, location.PayloadLength - byteOffset);

        var source = location.InPayloadArchive ? _payloadContent! : _content;
        lock (_contentLock)
        {
            source.Position = location.PayloadOffset + byteOffset;
            var read = 0;
            while (read < toRead)
            {
                var n = source.Read(buffer, offset + read, toRead - read);
                if (n == 0)
                    throw new EndOfStreamException("pxar payload ended before the mount read was satisfied");
                read += n;
            }

            return read;
        }
    }

    /// <summary>Canonical lookup key: forward slashes, no leading/trailing slash; <c>""</c> for the root.</summary>
    public static string NormalizePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        return relativePath.Replace('\\', '/').Trim('/');
    }

    private static Dictionary<string, PxarCatalogEntry> BuildCatalogMap(PxarCatalogEntry root)
    {
        var map = new Dictionary<string, PxarCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = root,
        };

        AddChildren(root, string.Empty, map);
        return map;
    }

    private static void AddChildren(PxarCatalogEntry parent, string parentPath, Dictionary<string, PxarCatalogEntry> map)
    {
        foreach (var child in parent.Children)
        {
            var path = parentPath.Length == 0 ? child.Name : $"{parentPath}/{child.Name}";
            map[path] = child;
            if (child.Type == PxarCatalogEntryType.Directory)
                AddChildren(child, path, map);
        }
    }
}
