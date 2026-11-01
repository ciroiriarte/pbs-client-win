namespace Pbs.Pxar;

/// <summary>
/// Sink for a split archive's payload stream: plain bytes go through the chunker; an injection
/// appends previous-snapshot chunks as-is and must start on a chunk boundary.
/// </summary>
public interface IPayloadSink
{
    /// <summary>Append encoded bytes at the current position.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>End the current chunk at the current position, then append <paramref name="segment"/>'s chunks.</summary>
    ValueTask InjectAsync(InjectChunksSegment segment, CancellationToken ct);
}

/// <summary>
/// Writes a split archive's payload stream (<c>.ppxar</c>) from a <see cref="PayloadPlan"/>:
/// <c>PAYLOAD_START_MARKER</c>, then each segment (a file's PAYLOAD header + data, or injected
/// chunks), then <c>PAYLOAD_TAIL_MARKER</c>. Every file must land at the offset the plan gave its
/// PAYLOAD_REF, so positions are checked as they are written; a file is read for exactly its planned
/// length (a file that shrank fails the backup instead of shifting every later offset).
/// </summary>
public static class PxarPayloadWriter
{
    public static async Task WriteAsync(PayloadPlan plan, IPayloadSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);
        long pos = 0;

        async ValueTask Write(ReadOnlyMemory<byte> data)
        {
            await sink.WriteAsync(data, ct);
            pos += data.Length;
        }

        await Write(Marker(PxarFormat.PayloadStartMarker));
        var buffer = new byte[1 << 20];
        foreach (var segment in plan.Segments)
        {
            switch (segment)
            {
                case EncodeFileSegment { File: var file }:
                    if (pos != plan.OffsetOf(file))
                        throw new InvalidOperationException($"pxar: payload of '{file.Name}' at {pos}, planned {plan.OffsetOf(file)}");
                    await Write(PxarEncoder.BuildPayloadHeader(file.Length));
                    await using (var src = file.OpenRead())
                    {
                        long remaining = file.Length;
                        while (remaining > 0)
                        {
                            int read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                            if (read == 0) throw new EndOfStreamException($"file '{file.Name}' shorter than reported length");
                            await Write(buffer.AsMemory(0, read));
                            remaining -= read;
                        }
                    }
                    break;

                case InjectChunksSegment inject:
                    await sink.InjectAsync(inject, ct);
                    pos += inject.TotalSize;
                    break;

                default:
                    throw new NotSupportedException(segment.GetType().Name);
            }
        }

        if (pos != plan.EndPosition)
            throw new InvalidOperationException($"pxar: payload stream ended at {pos}, planned {plan.EndPosition}");
        await Write(Marker(PxarFormat.PayloadTailMarker));
    }

    private static byte[] Marker(ulong htype)
    {
        var header = new byte[PxarFormat.HeaderSize];
        PxarEncoder.WriteHeaderInto(header, htype, PxarFormat.HeaderSize);
        return header;
    }
}
