using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Pbs.Cli;
using Pbs.Nbd;
using Pbs.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

public class NbdIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public NbdIntegrationTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task NbdServer_ReaderSessionBlockSource_ReadsFixedImageRanges()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        var image = new byte[5 * 1024 * 1024 + 12345];
        new Random(20260810).NextBytes(image);

        const string archive = "disk0.img.fidx";
        var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var backupId = $"nbd-it-{backupTime}";

        await using (var session = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime))
        {
            await FixedImage.UploadAsync(session, archive, new MemoryStream(image), image.Length);
            await session.FinishAsync();
        }

        await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);
        var size = await reader.GetArchiveSizeAsync(archive);
        var source = new ReaderSessionBlockSource(reader, archive, crypt: null, size);
        var server = new NbdServer(source, archive);
        using var cts = new CancellationTokenSource();
        var listenTask = server.ListenAsync(IPAddress.Loopback, 0, cts.Token);
        while (server.LocalEndPoint is null && !listenTask.IsCompleted)
            await Task.Delay(10);

        using var client = await NbdClient.ConnectAsync(server.LocalEndPoint!, archive);
        foreach (var (offset, length) in new (long, int)[] { (0, 4096), (123, 9001), (4 * 1024 * 1024 - 17, 65536), (image.Length - 777, 777) })
        {
            var actual = await client.ReadAsync(offset, length);
            Assert.True(image.AsSpan((int)offset, length).SequenceEqual(actual), $"NBD read {offset}+{length} mismatch");
        }

        _out.WriteLine("NBD fixed-image ranges matched source bytes");
        await client.DisconnectAsync();
        cts.Cancel();
        await listenTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class NbdClient : IDisposable
    {
        private const ulong NbdMagic = 0x4e42444d41474943;
        private const ulong IhaveOpt = 0x49484156454f5054;
        private const uint OptGo = 7;
        private const ulong OptionReplyMagic = 0x0003e889045565a9;
        private const uint RepAck = 1;
        private const uint RepInfo = 3;
        private const uint RequestMagic = 0x25609513;
        private const uint ReplyMagic = 0x67446698;
        private const ushort CmdRead = 0;
        private const ushort CmdDisc = 2;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private ulong _nextHandle = 1;

        private NbdClient(TcpClient tcp, NetworkStream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        public static async Task<NbdClient> ConnectAsync(IPEndPoint endpoint, string exportName)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port);
            var stream = tcp.GetStream();

            var handshake = new byte[18];
            await stream.ReadExactlyAsync(handshake);
            Assert.Equal(NbdMagic, ReadUInt64(handshake.AsSpan(0, 8)));
            Assert.Equal(IhaveOpt, ReadUInt64(handshake.AsSpan(8, 8)));

            var clientFlags = new byte[4];
            WriteUInt32(clientFlags, 3);
            await stream.WriteAsync(clientFlags);
            await SendOptionAsync(stream, OptGo, BuildInfoRequest(exportName));

            var info = await ReadOptionReplyAsync(stream);
            Assert.Equal(OptionReplyMagic, info.Magic);
            Assert.Equal(RepInfo, info.Type);
            var ack = await ReadOptionReplyAsync(stream);
            Assert.Equal(RepAck, ack.Type);

            return new NbdClient(tcp, stream);
        }

        public async Task<byte[]> ReadAsync(long offset, int length)
        {
            var handle = _nextHandle++;
            await SendRequestAsync(CmdRead, handle, offset, (uint)length);
            var reply = new byte[16];
            await _stream.ReadExactlyAsync(reply);
            Assert.Equal(ReplyMagic, ReadUInt32(reply.AsSpan(0, 4)));
            Assert.Equal(0u, ReadUInt32(reply.AsSpan(4, 4)));
            Assert.Equal(handle, ReadUInt64(reply.AsSpan(8, 8)));

            var data = new byte[length];
            await _stream.ReadExactlyAsync(data);
            return data;
        }

        public async Task DisconnectAsync() =>
            await SendRequestAsync(CmdDisc, _nextHandle++, 0, 0);

        public void Dispose()
        {
            _stream.Dispose();
            _tcp.Dispose();
        }

        private async Task SendRequestAsync(ushort type, ulong handle, long offset, uint length)
        {
            var request = new byte[28];
            WriteUInt32(request.AsSpan(0, 4), RequestMagic);
            WriteUInt16(request.AsSpan(6, 2), type);
            WriteUInt64(request.AsSpan(8, 8), handle);
            WriteUInt64(request.AsSpan(16, 8), (ulong)offset);
            WriteUInt32(request.AsSpan(24, 4), length);
            await _stream.WriteAsync(request);
        }

        private static async Task SendOptionAsync(Stream stream, uint option, byte[] payload)
        {
            var header = new byte[16];
            WriteUInt64(header.AsSpan(0, 8), IhaveOpt);
            WriteUInt32(header.AsSpan(8, 4), option);
            WriteUInt32(header.AsSpan(12, 4), (uint)payload.Length);
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
        }

        private static byte[] BuildInfoRequest(string exportName)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(exportName);
            var request = new byte[4 + name.Length + 2];
            WriteUInt32(request.AsSpan(0, 4), (uint)name.Length);
            name.CopyTo(request.AsSpan(4));
            return request;
        }

        private static async Task<(ulong Magic, uint Type)> ReadOptionReplyAsync(Stream stream)
        {
            var header = new byte[20];
            await stream.ReadExactlyAsync(header);
            var length = ReadUInt32(header.AsSpan(16, 4));
            if (length > 0)
                await stream.ReadExactlyAsync(new byte[length]);
            return (ReadUInt64(header.AsSpan(0, 8)), ReadUInt32(header.AsSpan(12, 4)));
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16BigEndian(span);
        private static uint ReadUInt32(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32BigEndian(span);
        private static ulong ReadUInt64(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64BigEndian(span);
        private static void WriteUInt16(Span<byte> span, int value) => BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
        private static void WriteUInt32(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32BigEndian(span, value);
        private static void WriteUInt64(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(span, value);
    }
}
