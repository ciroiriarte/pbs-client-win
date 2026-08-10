using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Pbs.Nbd;

namespace Pbs.Nbd.Tests;

public sealed class NbdServerTests
{
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong IhaveOpt = 0x49484156454f5054;
    private const ushort HandshakeFixedNewstyle = 1;
    private const ushort HandshakeNoZeroes = 2;
    private const uint ClientFixedNewstyle = 1;
    private const uint ClientNoZeroes = 2;
    private const uint OptExportName = 1;
    private const uint OptGo = 7;
    private const ulong OptionReplyMagic = 0x0003e889045565a9;
    private const uint RepAck = 1;
    private const uint RepInfo = 3;
    private const uint RequestMagic = 0x25609513;
    private const uint ReplyMagic = 0x67446698;
    private const ushort TransmissionHasFlags = 1;
    private const ushort TransmissionReadOnly = 2;
    private const ushort TransmissionSendFlush = 4;
    private const ushort CmdRead = 0;
    private const ushort CmdWrite = 1;
    private const ushort CmdDisc = 2;
    private const uint Eperm = 1;
    private const uint Einval = 22;

    [Fact]
    public async Task Handshake_FixedNewstyle_NbdOptGo_ReturnsExportSizeAndReadOnly()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        Assert.Equal(fixture.Data.Length, client.ExportSize);
        Assert.True((client.TransmissionFlags & TransmissionHasFlags) != 0);
        Assert.True((client.TransmissionFlags & TransmissionReadOnly) != 0);
        Assert.True((client.TransmissionFlags & TransmissionSendFlush) != 0);
    }

    [Fact]
    public async Task Read_AlignedRange_ReturnsExactBytes()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        var actual = await client.ReadAsync(4096, 8192);

        Assert.Equal(fixture.Data.AsSpan(4096, 8192).ToArray(), actual);
    }

    [Fact]
    public async Task Read_UnalignedAndCrossBoundary_ReturnsExactBytes()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        var actual = await client.ReadAsync(123, 9001);

        Assert.Equal(fixture.Data.AsSpan(123, 9001).ToArray(), actual);
    }

    [Fact]
    public async Task Read_OutOfBounds_RepliesEinval()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        var error = await client.ReadErrorAsync(fixture.Data.Length - 10, 11);

        Assert.Equal(Einval, error);
    }

    [Fact]
    public async Task Write_OnReadOnlyExport_RepliesEperm()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        var error = await client.WriteAsync(512, new byte[] { 1, 2, 3, 4 });

        Assert.Equal(Eperm, error);
    }

    [Fact]
    public async Task OptExportName_OldStyle_EntersTransmission_AndReads()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectWithExportNameAsync(fixture.EndPoint, fixture.ExportName);

        Assert.Equal(fixture.Data.Length, client.ExportSize);
        var actual = await client.ReadAsync(17, 321);

        Assert.Equal(fixture.Data.AsSpan(17, 321).ToArray(), actual);
    }

    [Fact]
    public async Task Disconnect_ClosesCleanly()
    {
        await using var fixture = await ServerFixture.StartAsync();
        using var client = await NbdTestClient.ConnectAsync(fixture.EndPoint, fixture.ExportName);

        await client.DisconnectAsync();
        var read = await client.Stream.ReadAsync(new byte[1]);

        Assert.Equal(0, read);
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _listenTask;

        private ServerFixture(byte[] data, string exportName, NbdServer server, Task listenTask, CancellationTokenSource cts)
        {
            Data = data;
            ExportName = exportName;
            EndPoint = server.LocalEndPoint ?? throw new InvalidOperationException("server did not bind");
            _listenTask = listenTask;
            _cts = cts;
        }

        public byte[] Data { get; }
        public string ExportName { get; }
        public IPEndPoint EndPoint { get; }

        public static async Task<ServerFixture> StartAsync()
        {
            var data = new byte[128 * 1024];
            for (var i = 0; i < data.Length; i++)
                data[i] = unchecked((byte)(((uint)i * 2654435761u) >> 13));

            var cts = new CancellationTokenSource();
            var source = new InMemoryBlockSource(data);
            var server = new NbdServer(source, "disk0.img.fidx");
            var listenTask = server.ListenAsync(IPAddress.Loopback, 0, cts.Token);

            while (server.LocalEndPoint is null && !listenTask.IsCompleted)
                await Task.Delay(10);
            if (listenTask.IsFaulted)
                await listenTask;

            return new ServerFixture(data, "disk0.img.fidx", server, listenTask, cts);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _listenTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private sealed class InMemoryBlockSource(byte[] data) : IBlockDeviceSource
    {
        public long Length => data.Length;
        public bool IsReadOnly => true;

        public ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            data.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NbdTestClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private ulong _nextHandle = 1;

        private NbdTestClient(TcpClient tcp, NetworkStream stream, long exportSize, ushort transmissionFlags)
        {
            _tcp = tcp;
            Stream = stream;
            ExportSize = exportSize;
            TransmissionFlags = transmissionFlags;
        }

        public NetworkStream Stream { get; }
        public long ExportSize { get; }
        public ushort TransmissionFlags { get; }

        public static async Task<NbdTestClient> ConnectAsync(IPEndPoint endpoint, string exportName)
        {
            var (tcp, stream) = await ConnectHandshakeAsync(endpoint);
            await SendOptionAsync(stream, OptGo, BuildInfoRequest(exportName));

            var info = await ReadOptionReplyAsync(stream);
            Assert.Equal(OptionReplyMagic, info.Magic);
            Assert.Equal(OptGo, info.Option);
            Assert.Equal(RepInfo, info.Type);
            Assert.Equal(12, info.Payload.Length);
            Assert.Equal(0, ReadUInt16(info.Payload.AsSpan(0, 2)));
            var exportSize = (long)ReadUInt64(info.Payload.AsSpan(2, 8));
            var flags = ReadUInt16(info.Payload.AsSpan(10, 2));

            var ack = await ReadOptionReplyAsync(stream);
            Assert.Equal(OptionReplyMagic, ack.Magic);
            Assert.Equal(OptGo, ack.Option);
            Assert.Equal(RepAck, ack.Type);
            Assert.Empty(ack.Payload);

            return new NbdTestClient(tcp, stream, exportSize, flags);
        }

        public static async Task<NbdTestClient> ConnectWithExportNameAsync(IPEndPoint endpoint, string exportName)
        {
            var (tcp, stream) = await ConnectHandshakeAsync(endpoint);
            await SendOptionAsync(stream, OptExportName, System.Text.Encoding.UTF8.GetBytes(exportName));

            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply);
            var exportSize = (long)ReadUInt64(reply.AsSpan(0, 8));
            var flags = ReadUInt16(reply.AsSpan(8, 2));
            return new NbdTestClient(tcp, stream, exportSize, flags);
        }

        public async Task<byte[]> ReadAsync(long offset, int length)
        {
            var handle = _nextHandle++;
            await SendRequestAsync(CmdRead, handle, offset, (uint)length);
            var reply = await ReadSimpleReplyAsync();
            Assert.Equal(ReplyMagic, reply.Magic);
            Assert.Equal(0u, reply.Error);
            Assert.Equal(handle, reply.Handle);
            var data = new byte[length];
            await Stream.ReadExactlyAsync(data);
            return data;
        }

        public async Task<uint> ReadErrorAsync(long offset, int length)
        {
            var handle = _nextHandle++;
            await SendRequestAsync(CmdRead, handle, offset, (uint)length);
            var reply = await ReadSimpleReplyAsync();
            Assert.Equal(ReplyMagic, reply.Magic);
            Assert.Equal(handle, reply.Handle);
            return reply.Error;
        }

        public async Task<uint> WriteAsync(long offset, byte[] data)
        {
            var handle = _nextHandle++;
            await SendRequestAsync(CmdWrite, handle, offset, (uint)data.Length);
            await Stream.WriteAsync(data);
            var reply = await ReadSimpleReplyAsync();
            Assert.Equal(ReplyMagic, reply.Magic);
            Assert.Equal(handle, reply.Handle);
            return reply.Error;
        }

        public async Task DisconnectAsync() =>
            await SendRequestAsync(CmdDisc, _nextHandle++, 0, 0);

        public void Dispose()
        {
            Stream.Dispose();
            _tcp.Dispose();
        }

        private async Task SendRequestAsync(ushort type, ulong handle, long offset, uint length)
        {
            var request = new byte[28];
            WriteUInt32(request.AsSpan(0, 4), RequestMagic);
            WriteUInt16(request.AsSpan(4, 2), 0);
            WriteUInt16(request.AsSpan(6, 2), type);
            WriteUInt64(request.AsSpan(8, 8), handle);
            WriteUInt64(request.AsSpan(16, 8), (ulong)offset);
            WriteUInt32(request.AsSpan(24, 4), length);
            await Stream.WriteAsync(request);
        }

        private async Task<(uint Magic, uint Error, ulong Handle)> ReadSimpleReplyAsync()
        {
            var reply = new byte[16];
            await Stream.ReadExactlyAsync(reply);
            return (ReadUInt32(reply.AsSpan(0, 4)), ReadUInt32(reply.AsSpan(4, 4)), ReadUInt64(reply.AsSpan(8, 8)));
        }

        private static async Task<(TcpClient Tcp, NetworkStream Stream)> ConnectHandshakeAsync(IPEndPoint endpoint)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port);
            var stream = tcp.GetStream();

            var handshake = new byte[18];
            await stream.ReadExactlyAsync(handshake);
            Assert.Equal(NbdMagic, ReadUInt64(handshake.AsSpan(0, 8)));
            Assert.Equal(IhaveOpt, ReadUInt64(handshake.AsSpan(8, 8)));
            Assert.Equal(HandshakeFixedNewstyle | HandshakeNoZeroes, ReadUInt16(handshake.AsSpan(16, 2)));

            var clientFlags = new byte[4];
            WriteUInt32(clientFlags, ClientFixedNewstyle | ClientNoZeroes);
            await stream.WriteAsync(clientFlags);
            return (tcp, stream);
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
            WriteUInt16(request.AsSpan(4 + name.Length, 2), 0);
            return request;
        }

        private static async Task<(ulong Magic, uint Option, uint Type, byte[] Payload)> ReadOptionReplyAsync(Stream stream)
        {
            var header = new byte[20];
            await stream.ReadExactlyAsync(header);
            var length = ReadUInt32(header.AsSpan(16, 4));
            var payload = new byte[length];
            if (length > 0)
                await stream.ReadExactlyAsync(payload);
            return (ReadUInt64(header.AsSpan(0, 8)), ReadUInt32(header.AsSpan(8, 4)), ReadUInt32(header.AsSpan(12, 4)), payload);
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt16BigEndian(span);
    private static uint ReadUInt32(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt32BigEndian(span);
    private static ulong ReadUInt64(ReadOnlySpan<byte> span) => BinaryPrimitives.ReadUInt64BigEndian(span);
    private static void WriteUInt16(Span<byte> span, int value) => BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value);
    private static void WriteUInt32(Span<byte> span, uint value) => BinaryPrimitives.WriteUInt32BigEndian(span, value);
    private static void WriteUInt64(Span<byte> span, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(span, value);
}
