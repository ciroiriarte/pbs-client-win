using System.Buffers.Binary;
using System.Text;
using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

/// <summary>
/// Interop tests: hand-craft pxar byte streams like the official proxmox-backup-client produces —
/// with XATTR/ACL/FCAPS/unknown metadata records between ENTRY and content, plus device/FIFO/hardlink
/// entries — and verify the indexer skips metadata, resolves correct payload offsets, and classifies
/// every kind. (This client's own encoder writes the minimal subset; those are covered elsewhere.)
/// </summary>
public class PxarIndexerInteropTests
{
    private const ulong S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFLNK = 0xA000, S_IFCHR = 0x2000, S_IFIFO = 0x1000;
    private const ulong UnknownFutureRecord = 0xdeadbeefcafef00d;

    [Fact]
    public async Task BuildIndex_SkipsMetadataAndResolvesOffsets_ForOfficialStyleArchive()
    {
        var aContent = Encoding.ASCII.GetBytes("HELLO-A-payload");
        var bContent = Encoding.ASCII.GetBytes("nested-b.bin-CONTENT-0123456789");

        var ms = new MemoryStream();
        Record(ms, PxarFormat.FormatVersion, new byte[8]);              // leading non-ENTRY record
        Record(ms, PxarFormat.Entry, Stat(S_IFDIR | 0x1ED, 1000));      // root dir
        Record(ms, PxarFormat.Xattr, Ascii("user.root=1"));            // metadata on the root dir

        // a regular file, preceded by two metadata records
        Record(ms, PxarFormat.Filename, Name("a.txt"));
        Record(ms, PxarFormat.Entry, Stat(S_IFREG | 0x1A4, 1001));
        Record(ms, PxarFormat.Xattr, Ascii("user.k=v"));
        Record(ms, PxarFormat.Fcaps, new byte[12]);
        Record(ms, PxarFormat.Payload, aContent);

        // a symlink with metadata
        Record(ms, PxarFormat.Filename, Name("link"));
        Record(ms, PxarFormat.Entry, Stat(S_IFLNK | 0x1FF, 1002));
        Record(ms, PxarFormat.Xattr, Ascii("user.k=v"));
        Record(ms, PxarFormat.Symlink, Ascii("a.txt"));

        // a subdirectory (with its own ACL metadata) containing a nested file with an UNKNOWN record
        Record(ms, PxarFormat.Filename, Name("sub"));
        Record(ms, PxarFormat.Entry, Stat(S_IFDIR | 0x1ED, 1003));
        Record(ms, PxarFormat.AclUser, new byte[16]);
        Record(ms, PxarFormat.Filename, Name("b.bin"));
        Record(ms, PxarFormat.Entry, Stat(S_IFREG | 0x1A4, 1004));
        Record(ms, UnknownFutureRecord, new byte[24]);                 // forward-compat: unknown record skipped
        Record(ms, PxarFormat.Payload, bContent);
        Record(ms, PxarFormat.Goodbye, new byte[24]);                  // end of sub

        // a device node (has a DEVICE content record)
        Record(ms, PxarFormat.Filename, Name("dev"));
        Record(ms, PxarFormat.Entry, Stat(S_IFCHR | 0x1A4, 1005));
        Record(ms, PxarFormat.Xattr, Ascii("user.k=v"));
        Record(ms, PxarFormat.Device, new byte[16]);

        // a FIFO (no content record at all — next record belongs to the parent)
        Record(ms, PxarFormat.Filename, Name("pipe"));
        Record(ms, PxarFormat.Entry, Stat(S_IFIFO | 0x1A4, 1006));
        Record(ms, PxarFormat.Xattr, Ascii("user.k=v"));

        Record(ms, PxarFormat.Goodbye, new byte[24]);                  // end of root

        ms.Position = 0;
        var index = await PxarIndexer.BuildIndexAsync(ms);
        var byPath = index.ToDictionary(e => e.RelativePath);

        Assert.Equal(PxarEntryKind.Directory, byPath[""].Kind);
        Assert.Equal(PxarEntryKind.File, byPath["a.txt"].Kind);
        Assert.Equal(PxarEntryKind.Symlink, byPath["link"].Kind);
        Assert.Equal(PxarEntryKind.Directory, byPath["sub"].Kind);
        Assert.Equal(PxarEntryKind.File, byPath["sub/b.bin"].Kind);
        Assert.Equal(PxarEntryKind.Special, byPath["dev"].Kind);
        Assert.Equal(PxarEntryKind.Special, byPath["pipe"].Kind);

        // The recorded offsets must point at the exact payload bytes despite the intervening metadata.
        Assert.Equal(aContent, ReadAt(ms, byPath["a.txt"]));
        Assert.Equal(bContent, ReadAt(ms, byPath["sub/b.bin"]));

        // Non-files carry no payload.
        Assert.Equal(0, byPath["link"].PayloadLength);
        Assert.Equal(0, byPath["dev"].PayloadLength);
        Assert.Equal(0, byPath["pipe"].PayloadLength);
    }

    [Fact]
    public async Task BuildIndex_HardlinkEntry_IsSpecialWithNoPayload()
    {
        var ms = new MemoryStream();
        Record(ms, PxarFormat.Entry, Stat(S_IFDIR | 0x1ED, 1));
        Record(ms, PxarFormat.Filename, Name("target"));
        Record(ms, PxarFormat.Entry, Stat(S_IFREG | 0x1A4, 2));
        Record(ms, PxarFormat.Payload, Ascii("data"));
        Record(ms, PxarFormat.Filename, Name("hard"));
        Record(ms, PxarFormat.Entry, Stat(S_IFREG | 0x1A4, 3));        // hardlink: mode says regular file...
        Record(ms, PxarFormat.Hardlink, Ascii("/target"));            // ...but content is a HARDLINK record
        Record(ms, PxarFormat.Goodbye, new byte[24]);

        ms.Position = 0;
        var byPath = (await PxarIndexer.BuildIndexAsync(ms)).ToDictionary(e => e.RelativePath);

        Assert.Equal(PxarEntryKind.File, byPath["target"].Kind);
        Assert.Equal(PxarEntryKind.Special, byPath["hard"].Kind);
        Assert.Equal(0, byPath["hard"].PayloadLength);
    }

    // --- byte-level pxar builders ---

    private static void Record(MemoryStream ms, ulong htype, byte[] payload)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(header[..8], htype);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)(16 + payload.Length));
        ms.Write(header);
        ms.Write(payload);
    }

    private static byte[] Stat(ulong mode, long mtimeSecs)
    {
        var b = new byte[PxarFormat.StatSize]; // mode u64 | flags u64 | uid u32 | gid u32 | mtime i64 | nanos u32 | pad u32
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0, 8), mode);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24, 8), mtimeSecs);
        return b;
    }

    private static byte[] Name(string name) => Encoding.UTF8.GetBytes(name + "\0");
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");

    private static byte[] ReadAt(Stream s, PxarEntryLocation e)
    {
        s.Position = e.PayloadOffset;
        var buf = new byte[e.PayloadLength];
        s.ReadExactly(buf);
        return buf;
    }
}
