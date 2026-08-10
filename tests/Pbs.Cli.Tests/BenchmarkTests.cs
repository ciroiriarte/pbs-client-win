using Pbs.Cli;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Cli.Tests;

public class BenchmarkTests
{
    [Fact]
    public void LocalMeasurements_ReturnPositiveThroughput()
    {
        var buffer = Benchmark.CreateTestBuffer(256 * 1024);
        var budget = 1024L * 1024;

        var results = new[]
        {
            Benchmark.MeasureSha256(buffer, budget),
            Benchmark.MeasureContentDefinedChunking(buffer, budget),
            Benchmark.MeasureZstdCompression(buffer, budget),
            Benchmark.MeasureZstdDecompression(buffer, budget),
            Benchmark.MeasureAes256GcmEncrypt(buffer, budget),
            Benchmark.MeasureAes256GcmDecryptVerify(buffer, budget),
        };

        Assert.All(results, r => Assert.True(r.MegabytesPerSecond > 0, r.Name));
    }

    [Fact]
    public void ZstdCompression_RoundTripsAndReportsUsefulRatio()
    {
        var buffer = Benchmark.CreateTestBuffer(256 * 1024);
        var compressed = DataBlob.Encode(buffer, compress: true, compressionLevel: 1);
        var decoded = DataBlob.Decode(compressed);
        var result = Benchmark.MeasureZstdCompression(buffer, 1024L * 1024);

        Assert.Equal(buffer, decoded);
        Assert.NotNull(result.Ratio);
        Assert.InRange(result.Ratio.Value, double.Epsilon, 2.0);
    }
}
