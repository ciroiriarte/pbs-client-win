using Pbs.Cli;
using Xunit;

namespace Pbs.Cli.Tests;

/// <summary>
/// Regression tests for the pxar streaming bridge (#4). The critical property: a producer error must
/// reach the consumer as a FAULTED read, never a clean EOF — otherwise a truncated pxar would be
/// committed as a "successful" backup. (This is the exact defect the earlier inline pipe wiring had.)
/// </summary>
public class BridgeAsyncTests
{
    [Fact]
    public async Task BridgeAsync_HappyPath_ConsumerReceivesAllBytes()
    {
        var data = new byte[10_000];
        new Random(2).NextBytes(data);

        var got = await DynamicArchive.BridgeAsync(
            writer => writer.WriteAsync(data, 0, data.Length),
            async reader =>
            {
                using var ms = new MemoryStream();
                await reader.CopyToAsync(ms);
                return ms.ToArray();
            });

        Assert.Equal(data, got);
    }

    [Fact]
    public async Task BridgeAsync_ProducerThrowsMidWrite_ConsumerObservesError_NotCleanEof()
    {
        var partial = new byte[64];
        new Random(3).NextBytes(partial);

        long bytesSeenByConsumer = -1;

        var ex = await Assert.ThrowsAsync<IOException>(() => DynamicArchive.BridgeAsync<int>(
            async writer =>
            {
                await writer.WriteAsync(partial);       // some real bytes...
                await writer.FlushAsync();
                throw new IOException("source file vanished mid-encode"); // ...then a mid-stream failure
            },
            async reader =>
            {
                using var ms = new MemoryStream();
                await reader.CopyToAsync(ms);            // must throw, not return a short read
                bytesSeenByConsumer = ms.Length;
                return (int)ms.Length;
            }));

        Assert.Contains("vanished", ex.Message);
        Assert.Equal(-1, bytesSeenByConsumer);          // consumer never reached a "clean" EOF
    }

    [Fact]
    public async Task BridgeAsync_ProducerThrowsBeforeAnyWrite_StillFaults()
    {
        // Empty-archive corner: a failure before the first byte must also fault (never commit an
        // empty archive as a successful backup).
        var reachedEof = false;

        var ex = await Assert.ThrowsAsync<IOException>(() => DynamicArchive.BridgeAsync<int>(
            _ => throw new IOException("failed before writing anything"),
            async reader =>
            {
                using var ms = new MemoryStream();
                await reader.CopyToAsync(ms);
                reachedEof = true;
                return (int)ms.Length;
            }));

        Assert.Contains("before writing", ex.Message);
        Assert.False(reachedEof);
    }

    [Fact]
    public async Task BridgeAsync_ConsumerThrows_DoesNotDeadlock()
    {
        // A consumer that aborts early must not deadlock the producer (backpressure) or leak it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DynamicArchive.BridgeAsync<int>(
            async writer =>
            {
                var block = new byte[1024 * 1024];
                for (var i = 0; i < 64; i++) await writer.WriteAsync(block); // would block on backpressure
            },
            reader => throw new InvalidOperationException("consumer aborted")));

        Assert.Equal("consumer aborted", ex.Message);
    }
}
