using System.Security.Cryptography;
using Pbs.Abstractions;
using Pbs.Protocol;
using Xunit;

namespace Pbs.Protocol.Tests;

public class FileFormatTests
{
    // Re-derive each magic exactly as upstream does — sha256(label)[0..8] — and assert it equals
    // the hard-coded constant. This proves the constants are correct, not merely self-consistent.
    [Theory]
    [InlineData(FileFormats.Label.UncompressedBlob)]
    [InlineData(FileFormats.Label.CompressedBlob)]
    [InlineData(FileFormats.Label.EncryptedBlob)]
    [InlineData(FileFormats.Label.EncrComprBlob)]
    [InlineData(FileFormats.Label.FixedSizedChunkIndex)]
    [InlineData(FileFormats.Label.DynamicSizedChunkIndex)]
    [InlineData(FileFormats.Label.CatalogFile)]
    public void Magic_MatchesSha256OfLabel(string label)
    {
        var expected = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(label)).AsSpan(0, 8).ToArray();
        var actual = label switch
        {
            FileFormats.Label.UncompressedBlob => FileFormats.UncompressedBlobMagic,
            FileFormats.Label.CompressedBlob => FileFormats.CompressedBlobMagic,
            FileFormats.Label.EncryptedBlob => FileFormats.EncryptedBlobMagic,
            FileFormats.Label.EncrComprBlob => FileFormats.EncrComprBlobMagic,
            FileFormats.Label.FixedSizedChunkIndex => FileFormats.FixedSizedChunkIndexMagic,
            FileFormats.Label.DynamicSizedChunkIndex => FileFormats.DynamicSizedChunkIndexMagic,
            FileFormats.Label.CatalogFile => FileFormats.CatalogFileMagic,
            _ => throw new ArgumentOutOfRangeException(nameof(label)),
        };
        Assert.Equal(expected, actual);
    }
}

public class DataBlobTests
{
    [Fact]
    public void Uncompressed_RoundTrips()
    {
        var data = new byte[5000];
        new Random(1).NextBytes(data);
        var blob = DataBlob.Encode(data, compress: false);

        Assert.Equal(FileFormats.UncompressedBlobMagic, blob.AsSpan(0, 8).ToArray());
        Assert.Equal(data, DataBlob.Decode(blob));
    }

    [Fact]
    public void Compressible_UsesCompressedMagic_AndRoundTrips()
    {
        var data = new byte[20000]; // all zeros — highly compressible
        var blob = DataBlob.Encode(data, compress: true);

        Assert.Equal(FileFormats.CompressedBlobMagic, blob.AsSpan(0, 8).ToArray());
        Assert.True(blob.Length < data.Length, "compressed blob should be smaller than input");
        Assert.Equal(data, DataBlob.Decode(blob));
    }

    [Fact]
    public void Incompressible_FallsBackToUncompressed()
    {
        var data = new byte[16];
        new Random(2).NextBytes(data);
        var blob = DataBlob.Encode(data, compress: true);

        // Random 16 bytes won't shrink under zstd, so the encoder must keep it uncompressed.
        Assert.Equal(FileFormats.UncompressedBlobMagic, blob.AsSpan(0, 8).ToArray());
        Assert.Equal(data, DataBlob.Decode(blob));
    }

    [Fact]
    public void CorruptedPayload_FailsCrc()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        var blob = DataBlob.Encode(data, compress: false);
        blob[^1] ^= 0xFF; // flip a payload byte

        Assert.Throws<InvalidDataException>(() => DataBlob.Decode(blob));
    }
}
