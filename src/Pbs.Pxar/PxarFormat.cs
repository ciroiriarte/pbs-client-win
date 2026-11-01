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

    // pxar format v2 (split archives, issue #33). A split archive is a metadata stream
    // (<c>.mpxar</c>) whose regular files carry PAYLOAD_REF instead of PAYLOAD, plus a payload
    // stream (<c>.ppxar</c>) framed by PAYLOAD_START_MARKER … PAYLOAD_TAIL_MARKER.
    /// <summary>Optional record right after FORMAT_VERSION (v2 only), e.g. the client's exclude patterns.</summary>
    public const ulong Prelude = 0xe309d79d9f7b771b;
    /// <summary>Regular-file content reference into the payload stream: <c>offset:u64 | size:u64</c>.</summary>
    public const ulong PayloadRef = 0x419d3d6bc4ba977e;
    /// <summary>First record of a payload stream (empty body).</summary>
    public const ulong PayloadStartMarker = 0x834c68c2194a4ed2;
    /// <summary>Last record of a payload stream (empty body).</summary>
    public const ulong PayloadTailMarker = 0x6c72b78b984c81b5;
    /// <summary>Size of a PAYLOAD_REF body (offset + size).</summary>
    public const int PayloadRefSize = 16;

    // Per-entry metadata records that the official client writes between ENTRY and the content
    // record (PAYLOAD/SYMLINK/…). The decoder and indexer skip ANY non-structural record by size, so
    // they do not depend on this list being exhaustive. Values from upstream pxar/src/format/mod.rs.
    public const ulong Xattr = 0x0dab0229b57dcd03;
    public const ulong AclUser = 0x2ce8540a457d55b8;
    public const ulong AclGroup = 0x136e3eceb04c03ab;
    public const ulong AclGroupObj = 0x10868031e9582876;
    public const ulong AclDefault = 0xbbbb13415a6896f5;
    public const ulong AclDefaultUser = 0xc89357b40532cd1f;
    public const ulong AclDefaultGroup = 0xf90a8a5816038ffe;
    public const ulong Fcaps = 0x2da9dd9db5f7fb67;
    public const ulong QuotaProjId = 0xe07540e82f7d1cbb;

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

    // S_IFMT file-type bits (stat mode).
    public const ulong ModeFmt = 0xF000;
    public const ulong TypeDir = 0x4000;
    public const ulong TypeReg = 0x8000;
    public const ulong TypeLnk = 0xA000;
    public const ulong TypeFifo = 0x1000;
    public const ulong TypeChr = 0x2000;
    public const ulong TypeBlk = 0x6000;
    public const ulong TypeSock = 0xC000;

    /// <summary>Hash a directory-entry name exactly as pxar does (SipHash-2-4 over the raw bytes).</summary>
    public static ulong HashFilename(ReadOnlySpan<byte> name) => SipHash24.Compute(name, HashKey1, HashKey2);
}
