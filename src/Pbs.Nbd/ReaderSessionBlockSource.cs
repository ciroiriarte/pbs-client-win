using Pbs.Protocol;

namespace Pbs.Nbd;

public sealed class ReaderSessionBlockSource(
    ReaderSession reader,
    string archiveName,
    CryptConfig? crypt,
    long length) : IBlockDeviceSource
{
    public long Length { get; } = length;
    public bool IsReadOnly => true;

    public async ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        var bytes = await reader.ReadRangeAsync(archiveName, offset, buffer.Length, crypt, ct);
        if (bytes.Length != buffer.Length)
            throw new EndOfStreamException($"Reader returned {bytes.Length} bytes for {buffer.Length}-byte range.");
        bytes.CopyTo(buffer);
    }
}
