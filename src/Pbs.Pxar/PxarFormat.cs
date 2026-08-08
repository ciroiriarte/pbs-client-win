namespace Pbs.Pxar;

/// <summary>
/// pxar archive format constants (from upstream <c>pxar/src/format/mod.rs</c>). Every header is
/// <c>htype:u64-le | full_size:u64-le</c> where <c>full_size</c> counts the 16-byte header itself.
/// All values are little-endian.
/// </summary>
public static class PxarFormat
{
    public const ulong FormatVersion = 0x730f6c75df16a40d;
    public const ulong Entry = 0xd5956474e588acef;
    public const ulong Filename = 0x16701121063917b3;
    public const ulong Symlink = 0x27f971e7dbf5dc5f;
    public const ulong Device = 0x9fc9e906586d5ce9;
    public const ulong Hardlink = 0x51269c8422bd7275;
    public const ulong Payload = 0x28147a1b0b7c1a25;
    public const ulong Goodbye = 0x2fec4fa642d5731d;
    public const ulong GoodbyeTailMarker = 0xef5eed5b753e1555;

    /// <summary>SipHash-2-4 keys pxar uses to hash directory-entry names.</summary>
    public const ulong HashKey1 = 0x83ac3f1cfbb450db;
    public const ulong HashKey2 = 0xaa4f1b6879369fbd;

    /// <summary>Size in bytes of a header (htype + full_size).</summary>
    public const int HeaderSize = 16;

    /// <summary>Size in bytes of a serialized Stat (Entry payload).</summary>
    public const int StatSize = 40; // mode u64 + flags u64 + uid u32 + gid u32 + mtime(secs i64 + nanos u32 + pad u32)

    /// <summary>Size in bytes of a GoodbyeItem (hash + offset + size).</summary>
    public const int GoodbyeItemSize = 24;

    // POSIX mode bits synthesized for Windows sources until a richer metadata mapping lands.
    public const ulong ModeDir = 0x4000 | 0x1ED;   // S_IFDIR | 0755
    public const ulong ModeFile = 0x8000 | 0x1A4;  // S_IFREG | 0644
    public const ulong ModeSymlink = 0xA000 | 0x1FF; // S_IFLNK | 0777

    /// <summary>Hash a directory-entry name exactly as pxar does (SipHash-2-4 over the raw bytes).</summary>
    public static ulong HashFilename(ReadOnlySpan<byte> name) => SipHash24.Compute(name, HashKey1, HashKey2);
}
