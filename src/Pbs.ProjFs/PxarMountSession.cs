using System.Collections.Concurrent;
using Microsoft.Windows.ProjFS;
using Pbs.Pxar;

namespace Pbs.ProjFs;

/// <summary>
/// Read-only ProjFS <see cref="IPbsMountProvider"/> for a pxar archive. Serves structure and metadata
/// from the catalog and file bytes on demand from the shared <see cref="PxarMountModel"/> (which owns
/// the seekable content stream and serializes reads). ProjFS is the zero-install engine on Windows
/// client / Server-with-Desktop-Experience; WinFsp covers Server Core (issue #29).
/// </summary>
public sealed class PxarMountSession : IPbsMountProvider
{
    private static readonly byte[] ContentId = [1];
    private static readonly byte[] ProviderId = [14];

    private readonly string _root;
    private readonly PxarMountModel _model;
    private readonly ConcurrentDictionary<Guid, ActiveEnumeration> _activeEnumerations = new();
    private readonly VirtualizationInstance _instance;

    private bool _started;
    private volatile bool _stopping;

    public string Id => "projfs";

    /// <summary>
    /// Enter teardown: subsequent deletes are permitted so the owning process can clear its
    /// hydrated placeholders while the provider is still running, then call <see cref="Dispose"/>.
    /// </summary>
    public void PrepareForTeardown() => _stopping = true;

    public PxarMountSession(string virtualizationRootPath, PxarMountModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _root = Path.GetFullPath(virtualizationRootPath);
        _model = model;

        var notifications = new[]
        {
            new NotificationMapping(
                NotificationType.PreDelete | NotificationType.PreRename | NotificationType.PreCreateHardlink,
                string.Empty),
        };

        _instance = new VirtualizationInstance(_root, 0, 0, enableNegativePathCache: false, notifications);
        _instance.OnQueryFileName = QueryFileName;
        // Deny deletes while serving (read-only), but allow them during teardown so the owning
        // process can clear its hydrated placeholders BEFORE StopVirtualizing (placeholders are not
        // deletable once the provider has stopped — ERROR_ACCESS_DENIED).
        _instance.OnNotifyPreDelete = (_, _, _, _) => _stopping;
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
        if (!_model.TryGetEntry(relativePath, out var entry) || entry.Type != PxarCatalogEntryType.Directory)
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
        if (!_model.TryGetEntry(relativePath, out var entry))
            return HResult.FileNotFound;

        var time = ToDateTime(entry.MtimeSecs);
        // ProjFS requires the native (backslash) relative path here; the model's forward-slash keys
        // are only for our internal lookups. Passing a '/'-containing path makes WritePlaceholderInfo
        // produce ERROR_INVALID_NAME for nested entries.
        return _instance.WritePlaceholderInfo(
            relativePath,
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
        if (!_model.TryGetFileLocation(relativePath, out var location))
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
        _model.TryGetEntry(relativePath, out _) ? HResult.Ok : HResult.FileNotFound;

    // Fill the ProjFS write buffer from the shared model in bounded (64 KiB) chunks. The model does the
    // locked seek + read against the content stream; ProjFS supplies the aligned offset/length.
    private void CopyPayloadToWriteBuffer(PxarEntryLocation location, long relativeOffset, Stream output, uint length)
    {
        var buffer = new byte[Math.Min(64 * 1024, length)];
        var remaining = (int)length;
        var fileOffset = relativeOffset;

        output.Position = 0;
        while (remaining > 0)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var read = _model.ReadFile(location, fileOffset, buffer, 0, toRead);
            if (read == 0)
                throw new EndOfStreamException("pxar payload ended before ProjFS read was satisfied");

            output.Write(buffer, 0, read);
            remaining -= read;
            fileOffset += read;
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
