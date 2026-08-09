using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

/// <summary>
/// Decodes a pxar archive stream, extracting its files and directories under a target path.
/// Reads the format sequentially: optional <c>FORMAT_VERSION</c>, then the root <c>ENTRY</c> and
/// each directory body — <c>(FILENAME + child)*</c> terminated by a <c>GOODBYE</c> table. The
/// entry <c>mode</c> distinguishes directory / file / symlink.
/// </summary>
public sealed class PxarDecoder
{
    private const ulong ModeFmt = 0xF000;
    private const ulong ModeDir = 0x4000;
    private const ulong ModeLnk = 0xA000;

    private readonly Stream _in;
    private PxarDecoder(Stream input) => _in = input;

    /// <summary>Extract the archive in <paramref name="input"/> into <paramref name="targetDir"/>.</summary>
    public static async Task ExtractAsync(Stream input, string targetDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var dec = new PxarDecoder(input);

        var (htype, _) = await dec.ReadHeaderAsync(ct);
        if (htype == PxarFormat.FormatVersion)
        {
            await dec.ReadExactAsync(new byte[8], ct);       // version u64
            (htype, _) = await dec.ReadHeaderAsync(ct);
        }
        if (htype != PxarFormat.Entry)
            throw new InvalidDataException($"pxar: expected root ENTRY, got htype {htype:x16}");
        await dec.ReadStatAsync(ct);                          // root stat (ignored)

        await dec.ExtractDirBodyAsync(targetDir, ct);
    }

    private async Task ExtractDirBodyAsync(string dir, CancellationToken ct)
    {
        while (true)
        {
            var (htype, size) = await ReadHeaderAsync(ct);
            if (htype == PxarFormat.Goodbye)
            {
                await SkipAsync(size - PxarFormat.HeaderSize, ct); // discard the goodbye table
                return;
            }
            if (htype != PxarFormat.Filename)
                throw new InvalidDataException($"pxar: expected FILENAME, got htype {htype:x16}");

            var name = await ReadNameAsync(size, ct);
            var path = Path.Combine(dir, name);

            var (eh, _) = await ReadHeaderAsync(ct);
            if (eh != PxarFormat.Entry)
                throw new InvalidDataException($"pxar: expected ENTRY after FILENAME, got htype {eh:x16}");
            var mode = await ReadStatAsync(ct);

            if ((mode & ModeFmt) == ModeDir)
            {
                Directory.CreateDirectory(path);
                await ExtractDirBodyAsync(path, ct);
            }
            else if ((mode & ModeFmt) == ModeLnk)
            {
                var (lh, ls) = await ReadHeaderAsync(ct);
                if (lh != PxarFormat.Symlink)
                    throw new InvalidDataException($"pxar: expected SYMLINK, got htype {lh:x16}");
                var target = await ReadNameAsync(ls, ct);
                try { File.CreateSymbolicLink(path, target); } catch (IOException) { /* privilege/plat limits */ }
            }
            else // regular file
            {
                var (ph, ps) = await ReadHeaderAsync(ct);
                if (ph != PxarFormat.Payload)
                    throw new InvalidDataException($"pxar: expected PAYLOAD, got htype {ph:x16}");
                await ExtractPayloadAsync(path, ps - PxarFormat.HeaderSize, ct);
            }
        }
    }

    private async Task ExtractPayloadAsync(string path, long length, CancellationToken ct)
    {
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await _in.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new EndOfStreamException("pxar: truncated PAYLOAD");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private async Task<(ulong htype, long size)> ReadHeaderAsync(CancellationToken ct)
    {
        var buffer = new byte[PxarFormat.HeaderSize];
        await ReadExactAsync(buffer, ct);
        ulong htype = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (size < PxarFormat.HeaderSize) throw new InvalidDataException("pxar: header size underflow");
        return (htype, size);
    }

    private async Task<ulong> ReadStatAsync(CancellationToken ct)
    {
        var buffer = new byte[PxarFormat.StatSize];
        await ReadExactAsync(buffer, ct);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8)); // mode
    }

    private async Task<string> ReadNameAsync(long headerSize, CancellationToken ct)
    {
        int len = (int)(headerSize - PxarFormat.HeaderSize);
        var buffer = new byte[len];
        await ReadExactAsync(buffer, ct);
        int end = len > 0 && buffer[len - 1] == 0 ? len - 1 : len; // strip null terminator
        var name = Encoding.UTF8.GetString(buffer, 0, end);
        if (name.Contains('/') || name.Contains('\\') || name == ".." || name == ".")
            throw new InvalidDataException($"pxar: unsafe entry name '{name}'");
        return name;
    }

    private async Task SkipAsync(long count, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(count, 81920)];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await _in.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new EndOfStreamException("pxar: truncated while skipping");
            remaining -= read;
        }
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _in.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("pxar: unexpected end of archive");
            read += n;
        }
    }
}
