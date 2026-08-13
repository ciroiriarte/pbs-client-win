using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using Pbs.Pxar;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Pbs.WinFsp;

/// <summary>
/// Read-only WinFsp <see cref="IPbsMountProvider"/> for a pxar archive — the engine used on Windows
/// Server Core, where ProjFS is unavailable (issue #29). Structure and metadata come from the shared
/// <see cref="PxarMountModel"/> catalog; file bytes are served on demand from its ranged content
/// stream. The mount target may be a drive letter (e.g. <c>X:</c>) or an (existing or newly created)
/// directory. The volume is read-only: every entry carries FILE_ATTRIBUTE_READONLY and all mutating
/// operations return STATUS_MEDIA_WRITE_PROTECTED.
/// </summary>
/// <remarks>
/// The managed binding (<c>winfsp-msil.dll</c>, namespace <see cref="Fsp"/>) is supplied by the
/// <c>winfsp.net</c> NuGet package and ships in our output; the native WinFsp driver + <c>winfsp-x64.dll</c>
/// must be installed on the machine (detect-and-require — see <see cref="WinFspAvailability"/> and
/// <c>docs/WINFSP.md</c>). <c>FileNode</c> is the entry's normalized catalog path (a string); callbacks
/// re-resolve it against the model, so no per-open state is kept.
/// </remarks>
public sealed class WinFspMountProvider : FileSystemBase, IPbsMountProvider
{
    // NTSTATUS values (STATUS_SUCCESS / STATUS_OBJECT_NAME_NOT_FOUND / STATUS_END_OF_FILE /
    // STATUS_MEDIA_WRITE_PROTECTED) are inherited constants on FileSystemBase.

    private const uint FILE_ATTRIBUTE_READONLY = 0x0001;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0010;

    // 100-ns ticks between 1601-01-01 (FILETIME epoch) and 1970-01-01 (Unix epoch).
    private const ulong UnixEpochAsFileTime = 116444736000000000UL;

    private readonly string _mountPoint;
    private readonly PxarMountModel _model;
    private readonly FileSystemHost _host;
    private readonly byte[] _securityDescriptor;
    private readonly ulong _totalSize;
    private readonly ulong _volumeCreationTime;

    private bool _mounted;

    public string Id => "winfsp";

    /// <param name="mountPoint">Drive letter (<c>X:</c>) or directory to mount at.</param>
    /// <param name="model">Shared backing store (catalog + index + content stream).</param>
    public WinFspMountProvider(string mountPoint, PxarMountModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);
        ArgumentNullException.ThrowIfNull(model);

        _mountPoint = mountPoint;
        _model = model;
        _host = new FileSystemHost(this);

        // A minimal world-readable security descriptor: owner/group BUILTIN\Administrators, DACL grants
        // Everyone generic read + execute. Applied uniformly since the mount is read-only.
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FRFX;;;WD)");
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);

        _totalSize = TotalFileBytes(model.Root);
        _volumeCreationTime = ToFileTime(model.Root.MtimeSecs);
    }

    public void Start()
    {
        var status = _host.Mount(_mountPoint, null, false, 0);
        if (status < 0)
            throw new InvalidOperationException(
                $"WinFsp mount at '{_mountPoint}' failed (NTSTATUS 0x{status:X8}). " +
                "Ensure WinFsp is installed and the target drive letter/directory is free.");

        _mounted = true;
    }

    // WinFsp owns its own namespace, so there are no on-disk placeholders to pre-clear (unlike ProjFS).
    public void PrepareForTeardown()
    {
    }

    public void Dispose()
    {
        if (_mounted)
        {
            _host.Unmount();
            _mounted = false;
        }
    }

    public override int Init(object Host)
    {
        var host = (FileSystemHost)Host;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.VolumeCreationTime = _volumeCreationTime;
        host.VolumeSerialNumber = 0;
        // Content is immutable for the life of the mount, so metadata can be cached indefinitely.
        host.FileInfoTimeout = unchecked((uint)-1);
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.FileSystemName = "pbs-pxar";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        VolumeInfo.TotalSize = _totalSize;
        VolumeInfo.FreeSize = 0; // read-only: nothing free
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string FileName, out uint FileAttributes, ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        if (!_model.TryGetEntry(PxarMountModel.NormalizePath(FileName), out var entry))
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FileAttributes = ToAttributes(entry);
        if (SecurityDescriptor != null)
            SecurityDescriptor = _securityDescriptor;

        return STATUS_SUCCESS;
    }

    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object? FileNode,
        out object? FileDesc,
        out FileInfo FileInfo,
        out string? NormalizedName)
    {
        FileNode = null;
        FileDesc = null;
        NormalizedName = null;
        FileInfo = default;

        var key = PxarMountModel.NormalizePath(FileName);
        if (!_model.TryGetEntry(key, out var entry))
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FileNode = key;
        FileInfo = ToFileInfo(entry);
        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object FileNode, object FileDesc, out FileInfo FileInfo)
    {
        FileInfo = default;
        if (!_model.TryGetEntry((string)FileNode, out var entry))
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FileInfo = ToFileInfo(entry);
        return STATUS_SUCCESS;
    }

    public override int Read(object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length, out uint BytesTransferred)
    {
        BytesTransferred = 0;

        // Directories and payload-less entries (symlinks/special) have no content.
        if (!_model.TryGetFileLocation((string)FileNode, out var location))
            return STATUS_END_OF_FILE;

        if ((long)Offset >= location.PayloadLength)
            return STATUS_END_OF_FILE;

        var remaining = location.PayloadLength - (long)Offset;
        var toRead = (int)Math.Min(Length, (uint)Math.Min(int.MaxValue, remaining));

        var scratch = ArrayPool<byte>.Shared.Rent(toRead);
        try
        {
            var read = _model.ReadFile(location, (long)Offset, scratch, 0, toRead);
            Marshal.Copy(scratch, 0, Buffer, read);
            BytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public override bool ReadDirectoryEntry(
        object FileNode,
        object FileDesc,
        string Pattern,
        string Marker,
        ref object Context,
        out string? FileName,
        out FileInfo FileInfo)
    {
        if (Context is not IEnumerator<PxarCatalogEntry> enumerator)
        {
            IEnumerable<PxarCatalogEntry> children = _model.TryGetEntry((string)FileNode, out var dir)
                ? dir.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                : Array.Empty<PxarCatalogEntry>();

            // Resume after Marker (WinFsp re-issues the enumeration across separate directory queries
            // and passes the last name it saw). Our ordering is stable, so skip past it.
            if (!string.IsNullOrEmpty(Marker))
                children = children.SkipWhile(c => !string.Equals(c.Name, Marker, StringComparison.OrdinalIgnoreCase)).Skip(1);

            enumerator = children.GetEnumerator();
            Context = enumerator;
        }

        if (enumerator.MoveNext())
        {
            var child = enumerator.Current;
            FileName = child.Name;
            FileInfo = ToFileInfo(child);
            return true;
        }

        FileName = null;
        FileInfo = default;
        return false;
    }

    // --- read-only enforcement: reject every mutating operation ---

    public override int Create(
        string FileName, uint CreateOptions, uint GrantedAccess, uint FileAttributes,
        byte[] SecurityDescriptor, ulong AllocationSize, out object? FileNode, out object? FileDesc,
        out FileInfo FileInfo, out string? NormalizedName)
    {
        FileNode = null;
        FileDesc = null;
        NormalizedName = null;
        FileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Overwrite(object FileNode, object FileDesc, uint FileAttributes, bool ReplaceFileAttributes, ulong AllocationSize, out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Write(
        object FileNode, object FileDesc, IntPtr Buffer, ulong Offset, uint Length,
        bool WriteToEndOfFile, bool ConstrainedIo, out uint BytesTransferred, out FileInfo FileInfo)
    {
        BytesTransferred = 0;
        FileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetBasicInfo(object FileNode, object FileDesc, uint FileAttributes, ulong CreationTime, ulong LastAccessTime, ulong LastWriteTime, ulong ChangeTime, out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetFileSize(object FileNode, object FileDesc, ulong NewSize, bool SetAllocationSize, out FileInfo FileInfo)
    {
        FileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int CanDelete(object FileNode, object FileDesc, string FileName) => STATUS_MEDIA_WRITE_PROTECTED;

    public override int Rename(object FileNode, object FileDesc, string FileName, string NewFileName, bool ReplaceIfExists) => STATUS_MEDIA_WRITE_PROTECTED;

    // --- helpers ---

    private FileInfo ToFileInfo(PxarCatalogEntry entry)
    {
        var size = entry.Type == PxarCatalogEntryType.File ? (ulong)Math.Max(0, entry.Size) : 0UL;
        var time = ToFileTime(entry.MtimeSecs);

        return new FileInfo
        {
            FileAttributes = ToAttributes(entry),
            ReparseTag = 0,
            FileSize = size,
            AllocationSize = (size + 4095) / 4096 * 4096,
            CreationTime = time,
            LastAccessTime = time,
            LastWriteTime = time,
            ChangeTime = time,
            IndexNumber = 0,
            HardLinks = 0,
        };
    }

    // Directories are directories; everything else (files, and — since we serve no reparse data —
    // symlinks/special entries) is presented as a read-only file.
    private static uint ToAttributes(PxarCatalogEntry entry) =>
        entry.Type == PxarCatalogEntryType.Directory
            ? FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY
            : FILE_ATTRIBUTE_READONLY;

    private static ulong ToFileTime(long mtimeSecs) =>
        mtimeSecs <= 0 ? UnixEpochAsFileTime : UnixEpochAsFileTime + (ulong)mtimeSecs * 10_000_000UL;

    private static ulong TotalFileBytes(PxarCatalogEntry root)
    {
        ulong total = 0;
        var stack = new Stack<PxarCatalogEntry>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            if (entry.Type == PxarCatalogEntryType.File)
                total += (ulong)Math.Max(0, entry.Size);
            foreach (var child in entry.Children)
                stack.Push(child);
        }

        return total;
    }
}
