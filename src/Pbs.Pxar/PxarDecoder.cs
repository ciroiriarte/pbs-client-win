using System.Buffers.Binary;
using System.Text;

namespace Pbs.Pxar;

/// <summary>Outcome of an extraction: what was written, and what could not be represented.</summary>
/// <param name="Files">Regular files written.</param>
/// <param name="HardlinksCopied">Hardlinks restored as independent copies of their already-extracted target.</param>
/// <param name="HardlinksSkipped">Hardlinks whose target was not extracted (excluded by patterns), so nothing was written.</param>
/// <param name="SpecialSkipped">Devices, FIFOs and sockets: present in the archive, not representable on the target.</param>
public readonly record struct PxarExtractResult(int Files, int HardlinksCopied, int HardlinksSkipped, int SpecialSkipped);

/// <summary>
/// Decodes a pxar archive stream, extracting its files and directories under a target path.
/// Reads the format sequentially: optional <c>FORMAT_VERSION</c> (+ <c>PRELUDE</c>), then the root
/// <c>ENTRY</c> and each directory body — <c>(FILENAME + child)*</c> terminated by a <c>GOODBYE</c>
/// table. After each <c>ENTRY</c> any metadata record (XATTR / ACL / FCAPS / QUOTA / unknown) is
/// skipped before the content record; the stat mode decides directory / file / symlink / special.
/// <para>
/// Split archives (pxar v2, issue #33): pass the payload stream (<c>.ppxar</c>) alongside the metadata
/// stream (<c>.mpxar</c>). Regular files then carry <c>PAYLOAD_REF(offset, size)</c>, resolved by
/// skipping forward in the payload stream; offsets must be non-decreasing, and the gaps between them
/// (bytes of reused chunks belonging to no file) are discarded, matching upstream's decoder.
/// </para>
/// With include patterns, only matching files are written; non-matching payloads are still consumed
/// to keep the stream aligned.
/// </summary>
public sealed class PxarDecoder
{
    private readonly Stream _in;
    private readonly Stream? _payload;
    private long _payloadConsumed;
    private readonly string _root; // canonical extraction root; nothing may be written outside it
    private readonly PxarIncludes? _includes;
    private (ulong htype, long size)? _pushedBack;

    // relative archive path ("a/b.txt") -> extracted full path, for restoring hardlinks as copies
    private readonly Dictionary<string, string> _extracted = new(StringComparer.Ordinal);
    private int _files, _hardlinksCopied, _hardlinksSkipped, _specialSkipped;

    private PxarDecoder(Stream input, Stream? payload, string root, PxarIncludes? includes)
    {
        _in = input;
        _payload = payload;
        _root = root;
        _includes = includes;
    }

    /// <summary>Extract the archive in <paramref name="input"/> into <paramref name="targetDir"/>.</summary>
    public static Task<PxarExtractResult> ExtractAsync(Stream input, string targetDir, CancellationToken ct = default)
        => ExtractAsync(input, targetDir, includePatterns: null, ct);

    /// <summary>
    /// Extract from <paramref name="input"/> into <paramref name="targetDir"/>. When
    /// <paramref name="includePatterns"/> is non-empty, only entries whose relative path matches a
    /// pattern are written; other files' payloads are skipped (the stream stays aligned).
    /// </summary>
    public static Task<PxarExtractResult> ExtractAsync(Stream input, string targetDir, IReadOnlyList<string>? includePatterns, CancellationToken ct = default)
        => ExtractSplitAsync(input, payloadInput: null, targetDir, includePatterns, ct);

    /// <summary>
    /// Extract a pxar archive whose file contents may live in a separate payload stream. With
    /// <paramref name="payloadInput"/> null this is a regular (unified) archive.
    /// </summary>
    public static async Task<PxarExtractResult> ExtractSplitAsync(
        Stream metadataInput, Stream? payloadInput, string targetDir, IReadOnlyList<string>? includePatterns, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);
        var includes = includePatterns is { Count: > 0 } ? new PxarIncludes(includePatterns) : null;
        var dec = new PxarDecoder(metadataInput, payloadInput, root, includes);

        if (payloadInput is not null)
        {
            var (ph, ps) = await ReadHeaderFromAsync(payloadInput, ct);
            if (ph != PxarFormat.PayloadStartMarker)
                throw new InvalidDataException($"pxar: payload stream does not start with PAYLOAD_START_MARKER (got htype {ph:x16})");
            await SkipFromAsync(payloadInput, ps - PxarFormat.HeaderSize, ct);
            dec._payloadConsumed = ps;
        }

        var (htype, size) = await dec.ReadHeaderAsync(ct);
        if (htype == PxarFormat.FormatVersion)
        {
            await dec.SkipAsync(size - PxarFormat.HeaderSize, ct); // version u64
            (htype, size) = await dec.ReadHeaderAsync(ct);
            if (htype == PxarFormat.Prelude)
            {
                await dec.SkipAsync(size - PxarFormat.HeaderSize, ct);
                (htype, size) = await dec.ReadHeaderAsync(ct);
            }
        }
        if (htype != PxarFormat.Entry)
            throw new InvalidDataException($"pxar: expected root ENTRY, got htype {htype:x16}");
        var rootMode = await dec.ReadStatAsync(size, ct);
        if ((rootMode & PxarFormat.ModeFmt) != PxarFormat.TypeDir)
            throw new InvalidDataException("pxar: root ENTRY is not a directory");

        await dec.SkipEntryMetadataAsync(ct);
        await dec.ExtractDirBodyAsync(root, relativePrefix: "", ct);
        return new PxarExtractResult(dec._files, dec._hardlinksCopied, dec._hardlinksSkipped, dec._specialSkipped);
    }

    private async Task ExtractDirBodyAsync(string dir, string relativePrefix, CancellationToken ct)
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
            var relPath = relativePrefix.Length == 0 ? name : $"{relativePrefix}/{name}";

            var (eh, es) = await ReadHeaderAsync(ct);
            if (eh == PxarFormat.Hardlink)
            {
                // The only entry without its own ENTRY: offset:u64 | target path (archive-relative).
                await ExtractHardlinkAsync(path, relPath, name, es, ct);
                continue;
            }
            if (eh != PxarFormat.Entry)
                throw new InvalidDataException($"pxar: expected ENTRY after FILENAME, got htype {eh:x16}");
            var mode = await ReadStatAsync(es, ct);
            var type = mode & PxarFormat.ModeFmt;

            if (type == PxarFormat.TypeDir)
            {
                await SkipEntryMetadataAsync(ct);
                GuardTargetPath(path);
                if (_includes is null) Directory.CreateDirectory(path); // else created lazily under matched files
                await ExtractDirBodyAsync(path, relPath, ct);
                continue;
            }

            var (ch, cs) = await ReadContentHeaderAsync(ct);
            if (type == PxarFormat.TypeLnk)
            {
                if (ch != PxarFormat.Symlink)
                    throw new InvalidDataException($"pxar: expected SYMLINK, got htype {ch:x16}");
                var target = await ReadStringAsync(cs, ct);

                if (_includes is null || _includes.IsMatch(relPath, name))
                {
                    GuardTargetPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (!IsSafeSymlinkTarget(path, target))
                        throw new InvalidDataException($"pxar: unsafe symlink target '{target}' -> escapes extraction root");
                    try { File.CreateSymbolicLink(path, target); } catch (IOException) { /* privilege/plat limits */ }
                }
            }
            else if (type == PxarFormat.TypeReg)
            {
                bool wanted = _includes is null || _includes.IsMatch(relPath, name);
                if (wanted)
                {
                    GuardTargetPath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }

                if (ch == PxarFormat.Payload)
                {
                    long payloadSize = cs - PxarFormat.HeaderSize;
                    if (wanted) await CopyToFileAsync(_in, path, payloadSize, ct);
                    else await SkipAsync(payloadSize, ct);
                }
                else if (ch == PxarFormat.PayloadRef)
                {
                    var (offset, refSize) = await ReadPayloadRefAsync(cs, ct);
                    await SeekPayloadAsync(offset, refSize, ct);
                    if (wanted) await CopyToFileAsync(_payload!, path, refSize, ct);
                    else await SkipFromAsync(_payload!, refSize, ct);
                    _payloadConsumed += refSize;
                }
                else
                {
                    throw new InvalidDataException($"pxar: expected PAYLOAD or PAYLOAD_REF, got htype {ch:x16}");
                }

                if (wanted)
                {
                    _files++;
                    _extracted[relPath] = path;
                }
            }
            else
            {
                // Device (DEVICE record), FIFO or socket (no content record): kept in the archive, but
                // not representable as a file on the restore target.
                if (ch == PxarFormat.Device) await SkipAsync(cs - PxarFormat.HeaderSize, ct);
                else PushBack(ch, cs);
                if (_includes is null || _includes.IsMatch(relPath, name)) _specialSkipped++;
            }
        }
    }

    private async Task ExtractHardlinkAsync(string path, string relPath, string name, long recordSize, CancellationToken ct)
    {
        int len = checked((int)(recordSize - PxarFormat.HeaderSize));
        if (len < 8) throw new InvalidDataException("pxar: HARDLINK record too short");
        var body = new byte[len];
        await ReadExactAsync(_in, body, ct);
        int end = body[^1] == 0 ? len - 1 : len;
        var target = Encoding.UTF8.GetString(body, 8, end - 8).TrimStart('/');

        if (_includes is not null && !_includes.IsMatch(relPath, name))
            return;
        if (!_extracted.TryGetValue(target, out var targetPath))
        {
            _hardlinksSkipped++; // target excluded by patterns (or not a regular file): nothing to copy
            return;
        }
        GuardTargetPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(targetPath, path, overwrite: true);
        _hardlinksCopied++;
        _extracted[relPath] = path;
    }

    // Skip the per-entry metadata records after an ENTRY up to (not including) the next structural
    // record, which is pushed back for the caller.
    private async Task SkipEntryMetadataAsync(CancellationToken ct)
    {
        var (h, s) = await ReadContentHeaderAsync(ct);
        PushBack(h, s);
    }

    // The first structural record after an entry's metadata: content (PAYLOAD/PAYLOAD_REF/SYMLINK/
    // DEVICE) or the parent's next FILENAME/GOODBYE (for directories, FIFOs and sockets).
    private async Task<(ulong htype, long size)> ReadContentHeaderAsync(CancellationToken ct)
    {
        while (true)
        {
            var (h, s) = await ReadHeaderAsync(ct);
            if (IsStructural(h)) return (h, s);
            await SkipAsync(s - PxarFormat.HeaderSize, ct);
        }
    }

    private static bool IsStructural(ulong htype) =>
        htype == PxarFormat.Filename || htype == PxarFormat.Goodbye || htype == PxarFormat.Payload ||
        htype == PxarFormat.PayloadRef || htype == PxarFormat.Symlink || htype == PxarFormat.Device ||
        htype == PxarFormat.Hardlink || htype == PxarFormat.Entry;

    private async Task<(ulong offset, long size)> ReadPayloadRefAsync(long recordSize, CancellationToken ct)
    {
        if (recordSize != PxarFormat.HeaderSize + PxarFormat.PayloadRefSize)
            throw new InvalidDataException("pxar: malformed PAYLOAD_REF");
        if (_payload is null)
            throw new InvalidDataException("pxar: archive references a payload stream (split .mpxar/.ppxar archive), but none was given");
        var buffer = new byte[PxarFormat.PayloadRefSize];
        await ReadExactAsync(_in, buffer, ct);
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (size > long.MaxValue) throw new InvalidDataException("pxar: PAYLOAD_REF size overflow");
        return (offset, (long)size);
    }

    // Move the payload stream to the PAYLOAD header at `offset`, verify it, and leave the stream at
    // the start of the file data.
    private async Task SeekPayloadAsync(ulong offset, long size, CancellationToken ct)
    {
        if (offset > long.MaxValue || (long)offset < _payloadConsumed)
            throw new InvalidDataException($"pxar: PAYLOAD_REF offset {offset} is behind the payload stream position {_payloadConsumed}");
        await SkipFromAsync(_payload!, (long)offset - _payloadConsumed, ct);
        var (h, s) = await ReadHeaderFromAsync(_payload!, ct);
        if (h != PxarFormat.Payload || s != PxarFormat.HeaderSize + size)
            throw new InvalidDataException($"pxar: payload stream has no matching PAYLOAD header at offset {offset}");
        _payloadConsumed = (long)offset + PxarFormat.HeaderSize;
    }

    /// <summary>
    /// A symlink target is safe only if it is relative and, resolved against the link's own
    /// directory, still points inside the extraction root. Absolute/rooted (incl. drive and UNC)
    /// targets are rejected outright. This stops a malicious archive from planting a link that a
    /// later entry writes through to escape the target directory.
    /// </summary>
    private bool IsSafeSymlinkTarget(string linkPath, string target)
    {
        if (string.IsNullOrEmpty(target) || Path.IsPathRooted(target)) return false;
        var linkDir = Path.GetDirectoryName(linkPath)!;
        var resolved = Path.GetFullPath(Path.Combine(linkDir, target));
        return IsWithinRoot(resolved);
    }

    private bool IsWithinRoot(string fullPath)
    {
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, _root, StringComparison.Ordinal) ||
               fullPath.StartsWith(rootWithSep, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defense-in-depth (#7): never write <em>through</em> a pre-existing on-disk symlink/junction.
    /// Entry names are already restricted (no <c>/</c>, <c>\</c>, <c>..</c>, <c>.</c>), so the archive
    /// cannot spell an escaping path — but the extraction root may be non-empty and contain a reparse
    /// point planted before the restore. Following it would place a write outside the root. Refuse if
    /// any existing ancestor directory below the root is a reparse point, or if the target itself
    /// already exists as one. Called before every directory/file/symlink write. The root itself is not
    /// checked: a symlinked target directory is the caller's own choice, not an archive-controlled path.
    /// Scope: this defends against reparse points planted <em>before</em> the restore. It is a
    /// check-then-write guard, so it does not defend against an attacker with concurrent write access to
    /// the extraction area swapping a directory for a symlink between this check and the write (TOCTOU) —
    /// Windows/.NET expose no atomic no-follow create to close that, and it is outside this threat model.
    /// </summary>
    private void GuardTargetPath(string path)
    {
        for (var dir = Path.GetDirectoryName(path);
             dir is not null && !string.Equals(dir, _root, StringComparison.Ordinal) && IsWithinRoot(dir);
             dir = Path.GetDirectoryName(dir))
        {
            if (IsReparsePoint(dir))
                throw new InvalidDataException($"pxar: refusing to extract through pre-existing symlink/reparse point '{dir}'");
        }

        if (IsReparsePoint(path))
            throw new InvalidDataException($"pxar: refusing to overwrite pre-existing symlink/reparse point '{path}'");
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task CopyToFileAsync(Stream src, string path, long length, CancellationToken ct)
    {
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await src.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new EndOfStreamException("pxar: truncated PAYLOAD");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private void PushBack(ulong htype, long size)
    {
        if (_pushedBack is not null) throw new InvalidOperationException("pxar: double header push-back");
        _pushedBack = (htype, size);
    }

    private async Task<(ulong htype, long size)> ReadHeaderAsync(CancellationToken ct)
    {
        if (_pushedBack is { } pending)
        {
            _pushedBack = null;
            return pending;
        }
        return await ReadHeaderFromAsync(_in, ct);
    }

    private static async Task<(ulong htype, long size)> ReadHeaderFromAsync(Stream input, CancellationToken ct)
    {
        var buffer = new byte[PxarFormat.HeaderSize];
        await ReadExactAsync(input, buffer, ct);
        ulong htype = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8, 8));
        if (size < PxarFormat.HeaderSize) throw new InvalidDataException("pxar: header size underflow");
        if (size > long.MaxValue) throw new InvalidDataException("pxar: header size overflow");
        return (htype, (long)size);
    }

    private async Task<ulong> ReadStatAsync(long entryRecordSize, CancellationToken ct)
    {
        if (entryRecordSize < PxarFormat.HeaderSize + PxarFormat.StatSize)
            throw new InvalidDataException("pxar: ENTRY stat underflow");
        var buffer = new byte[PxarFormat.StatSize];
        await ReadExactAsync(_in, buffer, ct);
        await SkipAsync(entryRecordSize - PxarFormat.HeaderSize - PxarFormat.StatSize, ct);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8)); // mode
    }

    private async Task<string> ReadNameAsync(long headerSize, CancellationToken ct)
    {
        var name = await ReadStringAsync(headerSize, ct);
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name == ".." || name == ".")
            throw new InvalidDataException($"pxar: unsafe entry name '{name}'");
        return name;
    }

    private async Task<string> ReadStringAsync(long headerSize, CancellationToken ct)
    {
        int len = checked((int)(headerSize - PxarFormat.HeaderSize));
        var buffer = new byte[len];
        await ReadExactAsync(_in, buffer, ct);
        int end = len > 0 && buffer[len - 1] == 0 ? len - 1 : len; // strip null terminator
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    private Task SkipAsync(long count, CancellationToken ct) => SkipFromAsync(_in, count, ct);

    private static async Task SkipFromAsync(Stream input, long count, CancellationToken ct)
    {
        if (count < 0) throw new InvalidDataException("pxar: negative skip");
        if (count == 0) return;
        if (input.CanSeek)
        {
            if (input.Position + count > input.Length) throw new EndOfStreamException("pxar: truncated while skipping");
            input.Position += count;
            return;
        }
        var buffer = new byte[Math.Min(count, 81920)];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) throw new EndOfStreamException("pxar: truncated while skipping");
            remaining -= read;
        }
    }

    private static async Task ReadExactAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await input.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("pxar: unexpected end of archive");
            read += n;
        }
    }
}
