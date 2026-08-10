using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Pbs.Nbd;

public sealed class NbdServer(IBlockDeviceSource source, string exportName, ILogger? log = null)
{
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong IhaveOpt = 0x49484156454f5054;
    private const ushort HandshakeFixedNewstyle = 1;
    private const ushort HandshakeNoZeroes = 2;
    private const uint ClientNoZeroes = 2;

    private const uint OptExportName = 1;
    private const uint OptAbort = 2;
    private const uint OptList = 3;
    private const uint OptInfo = 6;
    private const uint OptGo = 7;

    private const ulong OptionReplyMagic = 0x0003e889045565a9;
    private const uint RepAck = 1;
    private const uint RepServer = 2;
    private const uint RepInfo = 3;
    private const uint RepErrUnsup = 0x80000001;
    private const uint RepErrUnknown = 0x80000006;

    private const ushort InfoExport = 0;
    private const ushort TransmissionHasFlags = 1;
    private const ushort TransmissionReadOnly = 2;
    private const ushort TransmissionSendFlush = 4;
    private const ushort TransmissionFlags = TransmissionHasFlags | TransmissionReadOnly | TransmissionSendFlush;

    private const uint RequestMagic = 0x25609513;
    private const uint ReplyMagic = 0x67446698;
    private const ushort CmdRead = 0;
    private const ushort CmdWrite = 1;
    private const ushort CmdDisc = 2;
    private const ushort CmdFlush = 3;
    private const ushort CmdTrim = 4;
    private const ushort CmdWriteZeroes = 6;

    private const uint Eperm = 1;
    private const uint Einval = 22;
    private const int MaxReadLength = 64 * 1024 * 1024;

    private TcpListener? _listener;

    public IPEndPoint? LocalEndPoint { get; private set; }

    public async Task ListenAsync(IPAddress bind, int port, CancellationToken ct)
    {
        var listener = new TcpListener(bind, port);
        _listener = listener;
        listener.Start();
        LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;

        using var registration = ct.Register(static state => ((TcpListener)state!).Stop(), listener);
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                connections.Add(Task.Run(async () =>
                {
                    await using var stream = client.GetStream();
                    using (client)
                    {
                        await ServeConnectionAsync(stream, ct);
                    }
                }, ct));
                connections.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            if (connections.Count > 0)
            {
                try
                {
                    await Task.WhenAll(connections);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (IOException) when (ct.IsCancellationRequested)
                {
                }
            }
        }
    }

    public async Task ServeConnectionAsync(Stream stream, CancellationToken ct)
    {
        var clientFlags = await SendHandshakeAsync(stream, ct);
        if (!await HandleOptionsAsync(stream, clientFlags, ct))
            return;

        // MVP: serve one request at a time so simple replies are never interleaved.
        while (!ct.IsCancellationRequested)
        {
            var header = new byte[28];
            try
            {
                await stream.ReadExactlyAsync(header, ct);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            if (ReadUInt32(header.AsSpan(0, 4)) != RequestMagic)
            {
                log?.LogWarning("Closing NBD connection after invalid request magic.");
                return;
            }

            var type = ReadUInt16(header.AsSpan(6, 2));
            var handle = ReadUInt64(header.AsSpan(8, 8));
            var offset = (long)ReadUInt64(header.AsSpan(16, 8));
            var length = ReadUInt32(header.AsSpan(24, 4));

            switch (type)
            {
                case CmdRead:
                    await HandleReadAsync(stream, handle, offset, length, ct);
                    break;
                case CmdDisc:
                    return;
                case CmdFlush:
                    await WriteSimpleReplyAsync(stream, 0, handle, ct);
                    break;
                case CmdWrite:
                    await DrainAsync(stream, length, ct);
                    await WriteSimpleReplyAsync(stream, Eperm, handle, ct);
                    break;
                case CmdTrim:
                case CmdWriteZeroes:
                    await WriteSimpleReplyAsync(stream, Eperm, handle, ct);
                    break;
                default:
                    await WriteSimpleReplyAsync(stream, Einval, handle, ct);
                    break;
            }
        }
    }

    private async Task<uint> SendHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[18];
        WriteUInt64(buffer.AsSpan(0, 8), NbdMagic);
        WriteUInt64(buffer.AsSpan(8, 8), IhaveOpt);
        WriteUInt16(buffer.AsSpan(16, 2), HandshakeFixedNewstyle | HandshakeNoZeroes);
        await stream.WriteAsync(buffer, ct);

        var flags = new byte[4];
        await stream.ReadExactlyAsync(flags, ct);
        return ReadUInt32(flags);
    }

    private async Task<bool> HandleOptionsAsync(Stream stream, uint clientFlags, CancellationToken ct)
    {
        while (true)
        {
            var header = new byte[16];
            await stream.ReadExactlyAsync(header, ct);
            if (ReadUInt64(header.AsSpan(0, 8)) != IhaveOpt)
                throw new InvalidDataException("invalid NBD option magic");

            var option = ReadUInt32(header.AsSpan(8, 4));
            var length = ReadUInt32(header.AsSpan(12, 4));
            var data = length == 0 ? [] : new byte[length];
            if (data.Length > 0)
                await stream.ReadExactlyAsync(data, ct);

            switch (option)
            {
                case OptExportName:
                    if (!MatchesExportName(data))
                    {
                        log?.LogWarning("Client requested unknown NBD export name.");
                        return false;
                    }
                    await WriteExportNameReplyAsync(stream, (clientFlags & ClientNoZeroes) != 0, ct);
                    return true;
                case OptInfo:
                    await HandleInfoOrGoAsync(stream, option, data, enterTransmission: false, ct);
                    break;
                case OptGo:
                    if (!await HandleInfoOrGoAsync(stream, option, data, enterTransmission: true, ct))
                        return false;
                    return true;
                case OptAbort:
                    await WriteOptionReplyAsync(stream, option, RepAck, ReadOnlyMemory<byte>.Empty, ct);
                    return false;
                case OptList:
                    await WriteListReplyAsync(stream, option, ct);
                    await WriteOptionReplyAsync(stream, option, RepAck, ReadOnlyMemory<byte>.Empty, ct);
                    break;
                default:
                    await WriteOptionReplyAsync(stream, option, RepErrUnsup, ReadOnlyMemory<byte>.Empty, ct);
                    break;
            }
        }
    }

    private async Task<bool> HandleInfoOrGoAsync(Stream stream, uint option, byte[] data, bool enterTransmission, CancellationToken ct)
    {
        if (!TryParseInfoRequest(data, out var name) || !string.Equals(name, exportName, StringComparison.Ordinal))
        {
            await WriteOptionReplyAsync(stream, option, RepErrUnknown, ReadOnlyMemory<byte>.Empty, ct);
            return !enterTransmission;
        }

        var payload = new byte[12];
        WriteUInt16(payload.AsSpan(0, 2), InfoExport);
        WriteUInt64(payload.AsSpan(2, 8), (ulong)source.Length);
        WriteUInt16(payload.AsSpan(10, 2), TransmissionFlags);
        await WriteOptionReplyAsync(stream, option, RepInfo, payload, ct);
        await WriteOptionReplyAsync(stream, option, RepAck, ReadOnlyMemory<byte>.Empty, ct);
        return true;
    }

    private async Task HandleReadAsync(Stream stream, ulong handle, long offset, uint length, CancellationToken ct)
    {
        if (length > MaxReadLength || offset < 0 || length > (ulong)int.MaxValue || !RangeInBounds(offset, length))
        {
            await WriteSimpleReplyAsync(stream, Einval, handle, ct);
            return;
        }

        var data = new byte[length];
        await source.ReadAsync(offset, data, ct);
        await WriteSimpleReplyAsync(stream, 0, handle, ct);
        await stream.WriteAsync(data, ct);
    }

    private bool RangeInBounds(long offset, uint length)
    {
        var unsignedOffset = (ulong)offset;
        var unsignedLength = (ulong)length;
        var unsignedSize = (ulong)source.Length;
        return unsignedOffset <= unsignedSize && unsignedLength <= unsignedSize - unsignedOffset;
    }

    private bool MatchesExportName(byte[] data)
    {
        var name = System.Text.Encoding.UTF8.GetString(data);
        return string.IsNullOrEmpty(name) || string.Equals(name, exportName, StringComparison.Ordinal);
    }

    private static bool TryParseInfoRequest(byte[] data, out string name)
    {
        name = string.Empty;
        if (data.Length < 6)
            return false;

        var nameLength = ReadUInt32(data.AsSpan(0, 4));
        if (nameLength > int.MaxValue || data.Length < 4 + nameLength + 2)
            return false;

        name = System.Text.Encoding.UTF8.GetString(data, 4, (int)nameLength);
        var requestCountOffset = 4 + (int)nameLength;
        var requestCount = ReadUInt16(data.AsSpan(requestCountOffset, 2));
        return data.Length == requestCountOffset + 2 + requestCount * 2;
    }

    private async Task WriteExportNameReplyAsync(Stream stream, bool noZeroes, CancellationToken ct)
    {
        var length = noZeroes ? 10 : 134;
        var buffer = new byte[length];
        WriteUInt64(buffer.AsSpan(0, 8), (ulong)source.Length);
        WriteUInt16(buffer.AsSpan(8, 2), TransmissionFlags);
        await stream.WriteAsync(buffer, ct);
    }

    private async Task WriteListReplyAsync(Stream stream, uint option, CancellationToken ct)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(exportName);
        var payload = new byte[4 + nameBytes.Length];
        WriteUInt32(payload.AsSpan(0, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(payload.AsSpan(4));
        await WriteOptionReplyAsync(stream, option, RepServer, payload, ct);
    }

    private static async Task WriteOptionReplyAsync(Stream stream, uint option, uint type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[20];
        WriteUInt64(header.AsSpan(0, 8), OptionReplyMagic);
        WriteUInt32(header.AsSpan(8, 4), option);
        WriteUInt32(header.AsSpan(12, 4), type);
        WriteUInt32(header.AsSpan(16, 4), (uint)payload.Length);
        await stream.WriteAsync(header, ct);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct);
    }

    private static async Task WriteSimpleReplyAsync(Stream stream, uint error, ulong handle, CancellationToken ct)
    {
        var reply = new byte[16];
        WriteUInt32(reply.AsSpan(0, 4), ReplyMagic);
        WriteUInt32(reply.AsSpan(4, 4), error);
        WriteUInt64(reply.AsSpan(8, 8), handle);
        await stream.WriteAsync(reply, ct);
    }

    private static async Task DrainAsync(Stream stream, uint length, CancellationToken ct)
    {
        var remaining = length;
        var buffer = new byte[Math.Min(81920, length)];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0)
                throw new EndOfStreamException();
            remaining -= (uint)read;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16BigEndian(span);
    private static uint ReadUInt32(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32BigEndian(span);
    private static ulong ReadUInt64(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64BigEndian(span);
    private static void WriteUInt16(Span<byte> span, int value) => BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
    private static void WriteUInt32(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32BigEndian(span, value);
    private static void WriteUInt64(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(span, value);
}
