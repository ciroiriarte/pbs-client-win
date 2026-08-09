using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pbs.Vss;

/// <summary>
/// Opens a volume device (typically a VSS shadow-copy device,
/// <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN</c>) for raw sequential reading and reports
/// its byte length. Volume reads must be sector-aligned in both offset and length, so this reads
/// through <see cref="RandomAccess"/> in aligned blocks (a plain buffered <see cref="FileStream"/>
/// issues unaligned reads near end-of-volume and fails with "the parameter is incorrect").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawVolume : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;

    private readonly SafeFileHandle _handle;

    /// <summary>Volume size in bytes as reported by IOCTL_DISK_GET_LENGTH_INFO.</summary>
    public long Length { get; }

    /// <summary>A forward-only stream over the volume that reads in sector-aligned blocks.</summary>
    public Stream Stream { get; }

    public RawVolume(string devicePath)
    {
        _handle = CreateFileW(devicePath, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"failed to open volume device '{devicePath}'");

        if (!DeviceIoControl(_handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, out long length, sizeof(long), out _, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(err, "IOCTL_DISK_GET_LENGTH_INFO failed");
        }

        Length = length;
        Stream = new AlignedVolumeStream(_handle, length);
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        out long lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// Reads a volume device in aligned 1 MiB blocks via positioned reads, buffering internally so
    /// callers can read in any size. Volume length is a sector multiple, so every block offset and
    /// length stays sector-aligned.
    /// </summary>
    private sealed class AlignedVolumeStream : Stream
    {
        private const int Block = 1 << 20; // 1 MiB, a multiple of any sector size

        private readonly SafeFileHandle _handle;
        private readonly long _length;
        private readonly byte[] _buffer = new byte[Block];
        private long _devPos;   // aligned device offset of the next block to read
        private int _bufLen;
        private int _bufPos;

        public AlignedVolumeStream(SafeFileHandle handle, long length)
        {
            _handle = handle;
            _length = length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
        {
            if (_bufPos >= _bufLen)
            {
                if (_devPos >= _length) return 0;
                int want = (int)Math.Min(Block, _length - _devPos);
                int got = await RandomAccess.ReadAsync(_handle, _buffer.AsMemory(0, want), _devPos, ct);
                if (got == 0) return 0; // end of readable volume (a VSS shadow can expose slightly
                                        // fewer bytes than the reported length — the final cluster)
                _bufLen = got; _bufPos = 0; _devPos += got;
            }
            int n = Math.Min(destination.Length, _bufLen - _bufPos);
            _buffer.AsSpan(_bufPos, n).CopyTo(destination.Span);
            _bufPos += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Length => _length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
