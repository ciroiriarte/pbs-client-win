using System.Collections.Concurrent;
using Microsoft.Windows.ProjFS;
using Pbs.Pxar;

namespace Pbs.ProjFs;

/// <summary>
/// Read-only ProjFS provider for a pxar archive. Milestone 1 serves content from a seekable,
/// hydrated pxar temp stream using <see cref="PxarEntryLocation"/> payload offsets; Milestone 2 can
/// replace the stream with a ReaderSession-backed ranged reader behind the same path lookup.
/// </summary>
public sealed class PxarMountSession : IDisposable
{
    private static readonly byte[] ContentId = [1];
    private static readonly byte[] ProviderId = [14];

    private readonly string _root;
    private readonly PxarCatalogEntry _catalog;
    private readonly Stream _content;
    private readonly Dictionary<string, PxarEntryLocation> _locations;
    private readonly Dictionary<string, PxarCatalogEntry> _catalogByPath;
    private readonly ConcurrentDictionary<Guid, ActiveEnumeration> _activeEnumerations = new();
    private readonly object _contentLock = new();
    private readonly VirtualizationInstance _instance;

    private bool _started;

    public PxarMountSession(
        string virtualizationRootPath,
        PxarCatalogEntry catalog,
        Stream pxarContent,
        IReadOnlyList<PxarEntryLocation> index)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pxarContent);
        ArgumentNullException.ThrowIfNull(index);

        if (!pxarContent.CanRead || !pxarContent.CanSeek)
            throw new ArgumentException("ProjFS M1 mount requires a readable, seekable pxar content stream", nameof(pxarContent));

        _root = Path.GetFullPath(virtualizationRootPath);
        _catalog = catalog;
        _content = pxarContent;
        _locations = index.ToDictionary(entry => NormalizePath(entry.RelativePath), StringComparer.OrdinalIgnoreCase);
        _catalogByPath = BuildCatalogMap(catalog);

        var notifications = new[]
        {
            new NotificationMapping(
                NotificationType.PreDelete | NotificationType.PreRename | NotificationType.PreCreateHardlink,
                string.Empty),
        };

        _instance = new VirtualizationInstance(_root, 0, 0, enableNegativePathCache: false, notifications);
        _instance.OnQueryFileName = QueryFileName;
        _instance.OnNotifyPreDelete = (_, _, _, _) => false;
        _instance.OnNotifyPreRename = (_, _, _, _) => false;
        _instance.OnNotifyPreCreateHardlink = (_, _, _, _) => false;
        _instance.OnNotifyFilePreConvertToFull = (_, _, _) => false;
    }

    public void Start()
    {
        var hr = _instance.StartVirtualizing(new RequiredCallbacks(this));
        if (hr != HResult.Ok)
            throw new InvalidOperationException($"ProjFS StartVirtualizing failed: {hr}");

        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;

        _instance.StopVirtualizing();
        _started = false;
    }

    public void Dispose()
    {
        Stop();
        _instance.Dispose();
    }

    private HResult StartDirectoryEnumeration(Guid enumerationId, string relativePath)
    {
        if (!TryGetEntry(relativePath, out var entry) || entry.Type != PxarCatalogEntryType.Directory)
            return HResult.FileNotFound;

        var items = entry.Children
            .OrderBy(child => child.Name, ProjFsNameComparer.Instance)
            .ToList();

        return _activeEnumerations.TryAdd(enumerationId, new ActiveEnumeration(items))
            ? HResult.Ok
            : HResult.InternalError;
    }

    private HResult EndDirectoryEnumeration(Guid enumerationId) =>
        _activeEnumerations.TryRemove(enumerationId, out _) ? HResult.Ok : HResult.InternalError;

    private HResult GetDirectoryEnumeration(
        Guid enumerationId,
        string filterFileName,
        bool restartScan,
        IDirectoryEnumerationResults result)
    {
        if (!_activeEnumerations.TryGetValue(enumerationId, out var enumeration))
            return HResult.InternalError;

        if (restartScan)
            enumeration.Restart(filterFileName);
        else
            enumeration.TrySaveFilter(filterFileName);

        var added = 0;
        while (enumeration.IsCurrentValid)
        {
            var child = enumeration.Current;
            if (!AddToEnumeration(result, child))
                return added == 0 ? HResult.InsufficientBuffer : HResult.Ok;

            added++;
            enumeration.MoveNext();
        }

        return HResult.Ok;
    }

    private HResult GetPlaceholderInfo(string relativePath)
    {
        if (!TryGetEntry(relativePath, out var entry))
            return HResult.FileNotFound;

        var time = ToDateTime(entry.MtimeSecs);
        return _instance.WritePlaceholderInfo(
            NormalizePath(relativePath),
            time,
            time,
            time,
            time,
            GetAttributes(entry),
            entry.Type == PxarCatalogEntryType.File ? entry.Size : 0,
            entry.Type == PxarCatalogEntryType.Directory,
            ContentId,
            ProviderId);
    }

    private HResult GetFileData(string relativePath, ulong byteOffset, uint length, Guid dataStreamId)
    {
        var key = NormalizePath(relativePath);
        if (!_locations.TryGetValue(key, out var location) || location.Kind != PxarEntryKind.File)
            return HResult.FileNotFound;

        if (byteOffset >= (ulong)location.PayloadLength)
            return HResult.Ok;

        try
        {
            var remaining = Math.Min((long)length, location.PayloadLength - (long)byteOffset);
            var currentOffset = byteOffset;

            while (remaining > 0)
            {
                var desired = (uint)Math.Min(64 * 1024, remaining);
                using var writeBuffer = _instance.CreateWriteBuffer(
                    currentOffset,
                    desired,
                    out var alignedOffset,
                    out var alignedLength);

                var toRead = (uint)Math.Min(alignedLength, location.PayloadLength - (long)alignedOffset);
                CopyPayloadToWriteBuffer(location, (long)alignedOffset, writeBuffer.Stream, toRead);

                var hr = _instance.WriteFileData(dataStreamId, writeBuffer, alignedOffset, toRead);
                if (hr != HResult.Ok)
                    return hr;

                currentOffset = alignedOffset + toRead;
                remaining = location.PayloadLength - (long)currentOffset;
                if (currentOffset >= byteOffset + length)
                    break;
            }

            return HResult.Ok;
        }
        catch (OutOfMemoryException)
        {
            return HResult.OutOfMemory;
        }
        catch (Exception)
        {
            return HResult.InternalError;
        }
    }

    private HResult QueryFileName(string relativePath) =>
        TryGetEntry(relativePath, out _) ? HResult.Ok : HResult.FileNotFound;

    private void CopyPayloadToWriteBuffer(PxarEntryLocation location, long relativeOffset, Stream output, uint length)
    {
        var buffer = new byte[Math.Min(64 * 1024, length)];
        var remaining = length;

        lock (_contentLock)
        {
            _content.Position = location.PayloadOffset + relativeOffset;
            output.Position = 0;

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = _content.Read(buffer, 0, toRead);
                if (read == 0)
                    throw new EndOfStreamException("pxar payload ended before ProjFS read was satisfied");

                output.Write(buffer, 0, read);
                remaining -= (uint)read;
            }
        }
    }

    private bool AddToEnumeration(IDirectoryEnumerationResults result, PxarCatalogEntry entry)
    {
        var time = ToDateTime(entry.MtimeSecs);
        return result.Add(
            entry.Name,
            entry.Type == PxarCatalogEntryType.File ? entry.Size : 0,
            entry.Type == PxarCatalogEntryType.Directory,
            GetAttributes(entry),
            time,
            time,
            time,
            time);
    }

    private bool TryGetEntry(string relativePath, out PxarCatalogEntry entry) =>
        _catalogByPath.TryGetValue(NormalizePath(relativePath), out entry!);

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

    private static string NormalizePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        return relativePath.Replace('\\', '/').Trim('/');
    }

    private static FileAttributes GetAttributes(PxarCatalogEntry entry)
    {
        var attributes = FileAttributes.ReadOnly;
        if (entry.Type == PxarCatalogEntryType.Directory)
            attributes |= FileAttributes.Directory;
        else if (entry.Type == PxarCatalogEntryType.Symlink)
            attributes |= FileAttributes.ReparsePoint;
        else
            attributes |= FileAttributes.Normal;

        return attributes;
    }

    private static DateTime ToDateTime(long mtimeSecs)
    {
        if (mtimeSecs <= 0)
            return DateTime.UnixEpoch;

        return DateTimeOffset.FromUnixTimeSeconds(mtimeSecs).UtcDateTime;
    }

    private sealed class RequiredCallbacks(PxarMountSession session) : IRequiredCallbacks
    {
        public HResult StartDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName) =>
            session.StartDirectoryEnumeration(enumerationId, relativePath);

        public HResult EndDirectoryEnumerationCallback(Guid enumerationId) =>
            session.EndDirectoryEnumeration(enumerationId);

        public HResult GetDirectoryEnumerationCallback(
            int commandId,
            Guid enumerationId,
            string filterFileName,
            bool restartScan,
            IDirectoryEnumerationResults result) =>
            session.GetDirectoryEnumeration(enumerationId, filterFileName, restartScan, result);

        public HResult GetPlaceholderInfoCallback(
            int commandId,
            string relativePath,
            uint triggeringProcessId,
            string triggeringProcessImageFileName) =>
            session.GetPlaceholderInfo(relativePath);

        public HResult GetFileDataCallback(
            int commandId,
            string relativePath,
            ulong byteOffset,
            uint length,
            Guid dataStreamId,
            byte[] contentId,
            byte[] providerId,
            uint triggeringProcessId,
            string triggeringProcessImageFileName) =>
            session.GetFileData(relativePath, byteOffset, length, dataStreamId);
    }

    private sealed class ActiveEnumeration
    {
        private readonly IReadOnlyList<PxarCatalogEntry> _entries;
        private int _index = -1;
        private string? _filter;

        public ActiveEnumeration(IReadOnlyList<PxarCatalogEntry> entries)
        {
            _entries = entries;
            MoveNext();
        }

        public bool IsCurrentValid => _index >= 0 && _index < _entries.Count;
        public PxarCatalogEntry Current => _entries[_index];

        public void Restart(string filter)
        {
            _index = -1;
            _filter = NormalizeFilter(filter);
            MoveNext();
        }

        public void TrySaveFilter(string filter)
        {
            if (_filter is not null)
                return;

            _filter = NormalizeFilter(filter);
            if (IsCurrentValid && !MatchesFilter(Current.Name))
                MoveNext();
        }

        public bool MoveNext()
        {
            do
            {
                _index++;
            }
            while (IsCurrentValid && !MatchesFilter(Current.Name));

            return IsCurrentValid;
        }

        private bool MatchesFilter(string name) =>
            string.IsNullOrEmpty(_filter) || _filter == "*" || Utils.IsFileNameMatch(name, _filter);

        private static string NormalizeFilter(string? filter) =>
            string.IsNullOrEmpty(filter) ? string.Empty : filter;
    }

    private sealed class ProjFsNameComparer : IComparer<string>
    {
        public static readonly ProjFsNameComparer Instance = new();
        public int Compare(string? x, string? y) => Utils.FileNameCompare(x ?? string.Empty, y ?? string.Empty);
    }
}
