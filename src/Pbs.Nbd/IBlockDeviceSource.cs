namespace Pbs.Nbd;

public interface IBlockDeviceSource
{
    long Length { get; }
    bool IsReadOnly { get; }
    ValueTask ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);
}
