using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

/// <summary>Entry type byte used by PBS catalog files (<c>.pcat1</c>).</summary>
public enum PxarCatalogEntryType : byte
{
    Directory = (byte)'d',
    File = (byte)'f',
    Symlink = (byte)'l',
    Hardlink = (byte)'h',
    BlockDevice = (byte)'b',
    CharDevice = (byte)'c',
    Fifo = (byte)'p',
    Socket = (byte)'s',
}

/// <summary>A parsed PBS catalog entry tree.</summary>
public sealed record PxarCatalogEntry(
    string Name,
    PxarCatalogEntryType Type,
    long Size,
    long MtimeSecs,
    IReadOnlyList<PxarCatalogEntry> Children);

/// <summary>A flattened parsed catalog entry with its full path.</summary>
public readonly record struct PxarCatalogItem(
    string Path,
    PxarCatalogEntryType Type,
    long? Size,
    long? MtimeSecs);

/// <summary>
/// Builds and parses PBS catalog files (<c>.pcat1</c>) from pxar trees. The binary layout matches
/// upstream <c>pbs-datastore/src/catalog.rs</c>: an 8-byte magic, then each directory block
/// (<c>varu64(table_len) | varu64(count) | entries</c>) written post-order (children before parent),
/// and a trailing little-endian u64 pointing at the root directory block. Directory entries store a
/// back-offset to their child block; file entries store size + mtime as varints.
/// </summary>
public static class PxarCatalog
{
    private const int MaxNameLength = 4096;

    // sha256("Proxmox Backup Catalog file v1.0")[0..8] — same value as FileFormats.CatalogFileMagic,
    // kept local so Pbs.Pxar stays independent of Pbs.Protocol.
    private static readonly byte[] CatalogMagic = { 145, 253, 96, 249, 196, 103, 88, 213 };

    /// <summary>Build a complete <c>.pcat1</c> payload from a pxar directory tree.</summary>
    public static byte[] Build(PxarDirNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using var output = new MemoryStream();
        output.Write(CatalogMagic);

        var rootStart = WriteDirectory(output, root);

        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, rootStart);
        output.Write(trailer);

        return output.ToArray();
    }

    /// <summary>Parse a complete <c>.pcat1</c> payload into a tree rooted at the (unnamed) top.</summary>
    public static PxarCatalogEntry Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("catalog shorter than magic plus root pointer");
        if (!data[..8].SequenceEqual(CatalogMagic))
            throw new InvalidDataException("not a PBS catalog (bad magic)");

        var rootStart = BinaryPrimitives.ReadUInt64LittleEndian(data[^8..]);
        if (rootStart < 8 || rootStart >= (ulong)(data.Length - 8))
            throw new InvalidDataException($"catalog root pointer out of range: {rootStart}");

        var children = ReadDirectory(data, rootStart);
        return new PxarCatalogEntry(string.Empty, PxarCatalogEntryType.Directory, 0, 0, children);
    }

    /// <summary>Enumerate all entries in depth-first order, excluding the synthetic root.</summary>
    public static IEnumerable<PxarCatalogItem> Enumerate(PxarCatalogEntry root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var child in root.Children)
            foreach (var item in Enumerate(child, string.Empty))
                yield return item;
    }

    private static IEnumerable<PxarCatalogItem> Enumerate(PxarCatalogEntry entry, string parent)
    {
        var path = parent.Length == 0 ? "/" + entry.Name : parent + "/" + entry.Name;
        yield return new PxarCatalogItem(
            path,
            entry.Type,
            entry.Type == PxarCatalogEntryType.File ? entry.Size : null,
            entry.Type == PxarCatalogEntryType.File ? entry.MtimeSecs : null);

        foreach (var child in entry.Children)
            foreach (var item in Enumerate(child, path))
                yield return item;
    }

    private static ulong WriteDirectory(Stream output, PxarDirNode dir)
    {
        var entries = new List<EncodedEntry>();

        foreach (var child in SortChildren(dir.Children))
        {
            switch (child)
            {
                case PxarDirNode childDir:
                    var childStart = WriteDirectory(output, childDir);
                    entries.Add(new EncodedEntry(PxarCatalogEntryType.Directory, child.Name, childStart, 0, 0));
                    break;

                case PxarFileNode file:
                    entries.Add(new EncodedEntry(PxarCatalogEntryType.File, child.Name, 0, (ulong)file.Length, file.MtimeSecs));
                    break;

                case PxarSymlinkNode:
                    entries.Add(new EncodedEntry(PxarCatalogEntryType.Symlink, child.Name, 0, 0, 0));
                    break;

                default:
                    throw new NotSupportedException(child.GetType().Name);
            }
        }

        var start = (ulong)output.Position;

        using var table = new MemoryStream();
        EncodeU64(table, (ulong)entries.Count);
        foreach (var entry in entries)
            WriteEntry(table, entry, start);

        var tableBytes = table.ToArray();
        EncodeU64(output, (ulong)tableBytes.Length);
        output.Write(tableBytes);

        return start;
    }

    private static IReadOnlyList<PxarCatalogEntry> ReadDirectory(ReadOnlySpan<byte> data, ulong start)
    {
        var pos = checked((int)start);
        var tableLength = DecodeU64(data, ref pos);
        if (tableLength < 1)
            throw new InvalidDataException($"catalog directory table at {start} has invalid length {tableLength}");
        if (tableLength > (ulong)(data.Length - 8 - pos))
            throw new InvalidDataException($"catalog directory table at {start} extends past file body");

        var end = checked(pos + (int)tableLength);
        var count = DecodeU64(data, ref pos);
        var entries = new List<PxarCatalogEntry>(checked((int)Math.Min(count, int.MaxValue)));

        for (ulong i = 0; i < count; i++)
        {
            if (pos >= end)
                throw new InvalidDataException("catalog directory table ended before entry count was satisfied");

            var type = ToEntryType(data[pos++]);
            var nameLength = DecodeU64(data, ref pos);
            if (nameLength >= MaxNameLength)
                throw new InvalidDataException($"catalog entry name too long ({nameLength} >= {MaxNameLength})");
            if (nameLength > (ulong)(end - pos))
                throw new InvalidDataException("catalog entry name extends past directory table");

            var name = Encoding.UTF8.GetString(data.Slice(pos, checked((int)nameLength)));
            pos += checked((int)nameLength);

            entries.Add(type switch
            {
                PxarCatalogEntryType.Directory => ReadDirectoryEntry(data, ref pos, start, name),
                PxarCatalogEntryType.File => ReadFileEntry(data, ref pos, name),
                _ => new PxarCatalogEntry(name, type, 0, 0, Array.Empty<PxarCatalogEntry>()),
            });
        }

        if (pos != end)
            throw new InvalidDataException("catalog directory table has trailing bytes");

        return entries;
    }

    private static PxarCatalogEntry ReadDirectoryEntry(ReadOnlySpan<byte> data, ref int pos, ulong parentStart, string name)
    {
        var offset = DecodeU64(data, ref pos);
        if (offset == 0 || offset > parentStart)
            throw new InvalidDataException($"catalog directory offset {offset} is invalid for parent at {parentStart}");

        var childStart = parentStart - offset;
        var children = ReadDirectory(data, childStart);
        return new PxarCatalogEntry(name, PxarCatalogEntryType.Directory, 0, 0, children);
    }

    private static PxarCatalogEntry ReadFileEntry(ReadOnlySpan<byte> data, ref int pos, string name)
    {
        var size = DecodeU64(data, ref pos);
        var mtime = DecodeI64(data, ref pos);
        if (size > long.MaxValue)
            throw new InvalidDataException($"catalog file size too large for this client: {size}");

        return new PxarCatalogEntry(name, PxarCatalogEntryType.File, (long)size, mtime, Array.Empty<PxarCatalogEntry>());
    }

    private static void WriteEntry(Stream output, EncodedEntry entry, ulong parentStart)
    {
        output.WriteByte((byte)entry.Type);

        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        if (nameBytes.Length >= MaxNameLength)
            throw new InvalidDataException($"catalog entry name too long ({nameBytes.Length} >= {MaxNameLength})");
        if (nameBytes.AsSpan().Contains((byte)0))
            throw new InvalidDataException("catalog entry name contains NUL");

        EncodeU64(output, (ulong)nameBytes.Length);
        output.Write(nameBytes);

        switch (entry.Type)
        {
            case PxarCatalogEntryType.Directory:
                if (entry.DirectoryStart >= parentStart)
                    throw new InvalidDataException("catalog child directory must be written before parent");
                EncodeU64(output, parentStart - entry.DirectoryStart);
                break;

            case PxarCatalogEntryType.File:
                EncodeU64(output, entry.Size);
                EncodeI64(output, entry.MtimeSecs);
                break;
        }
    }

    private static void EncodeU64(Stream output, ulong value)
    {
        var d = value;
        while (true)
        {
            if (d < 128)
            {
                output.WriteByte((byte)d);
                return;
            }

            output.WriteByte((byte)(0x80 | (d & 0x7f)));
            d >>= 7;
        }
    }

    private static ulong DecodeU64(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;

        for (int i = 0; i < 10; i++)
        {
            if (pos >= data.Length)
                throw new EndOfStreamException("catalog u64 varint truncated");

            var b = data[pos++];
            value |= ((ulong)(b & 0x7f)) << (i * 7);
            if (b < 128)
                return value;
        }

        throw new InvalidDataException("catalog u64 varint missing end marker");
    }

    private static void EncodeI64(Stream output, long value)
    {
        var d = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;

        while (true)
        {
            if (d < 128)
            {
                if (value < 0)
                {
                    output.WriteByte((byte)(0x80 | d));
                    output.WriteByte(0);
                }
                else
                {
                    output.WriteByte((byte)d);
                }

                return;
            }

            output.WriteByte((byte)(0x80 | (d & 0x7f)));
            d >>= 7;
        }
    }

    private static long DecodeI64(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;

        for (int i = 0; i < 11; i++)
        {
            if (pos >= data.Length)
                throw new EndOfStreamException("catalog i64 varint truncated");

            var b = data[pos++];
            if (b == 0)
            {
                if (value == 0)
                    return 0;
                return -((long)(value - 1)) - 1;
            }

            if (i == 10)
                throw new InvalidDataException("catalog i64 varint missing end marker");

            value |= ((ulong)(b & 0x7f)) << (i * 7);
            if (b < 128)
                return (long)value;
        }

        throw new InvalidDataException("catalog i64 varint missing end marker");
    }

    private static PxarCatalogEntryType ToEntryType(byte value) => value switch
    {
        (byte)'d' => PxarCatalogEntryType.Directory,
        (byte)'f' => PxarCatalogEntryType.File,
        (byte)'l' => PxarCatalogEntryType.Symlink,
        (byte)'h' => PxarCatalogEntryType.Hardlink,
        (byte)'b' => PxarCatalogEntryType.BlockDevice,
        (byte)'c' => PxarCatalogEntryType.CharDevice,
        (byte)'p' => PxarCatalogEntryType.Fifo,
        (byte)'s' => PxarCatalogEntryType.Socket,
        _ => throw new InvalidDataException($"invalid catalog entry type '{(char)value}'"),
    };

    private static IEnumerable<PxarNode> SortChildren(IEnumerable<PxarNode> children) =>
        children.OrderBy(c => Encoding.UTF8.GetBytes(c.Name), ByteArrayComparer.Instance);

    private readonly record struct EncodedEntry(
        PxarCatalogEntryType Type,
        string Name,
        ulong DirectoryStart,
        ulong Size,
        long MtimeSecs);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
