using System.Buffers.Binary;
using System.Text;
using Pbs.Pxar;
using Xunit;

namespace Pbs.Pxar.Tests;

public class SipHash24Tests
{
    // Reference SipHash-2-4 vectors (key = bytes 00..0f as two little-endian u64s).
    private const ulong K0 = 0x0706050403020100;
    private const ulong K1 = 0x0f0e0d0c0b0a0908;

    [Fact]
    public void EmptyInput_MatchesReferenceVector()
    {
        Assert.Equal(0x726fdb47dd0e0e31UL, SipHash24.Compute(ReadOnlySpan<byte>.Empty, K0, K1));
    }

    [Fact]
    public void FifteenBytes_MatchesReferenceVector()
    {
        // Input = bytes 0x00..0x0e (the canonical 15-byte SipHash-2-4 test vector).
        var input = new byte[15];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)i;
        Assert.Equal(0xa129ca6149be45e5UL, SipHash24.Compute(input, K0, K1));
    }
}

public class BinaryTreeArrayTests
{
    [Fact]
    public void Order_ForThreeItems_IsBalancedBst()
    {
        // sorted [0,1,2] -> BST array [middle, smaller, larger] = [1,0,2]
        var output = new int[3];
        BinaryTreeArray.Copy(3, (src, dst) => output[dst] = src);
        Assert.Equal(new[] { 1, 0, 2 }, output);
    }

    [Fact]
    public void Order_ForOneItem_IsIdentity()
    {
        var output = new int[1];
        BinaryTreeArray.Copy(1, (src, dst) => output[dst] = src);
        Assert.Equal(new[] { 0 }, output);
    }

    [Fact]
    public void Order_IsAPermutation()
    {
        for (int n = 1; n <= 33; n++)
        {
            var output = new int[n];
            var hit = new bool[n];
            BinaryTreeArray.Copy(n, (src, dst) => { output[dst] = src; hit[src] = true; });
            Assert.All(hit, h => Assert.True(h)); // every source index used exactly once
        }
    }
}

public class PxarEncoderTests
{
    // Minimal header reader for structural assertions.
    private static (ulong htype, ulong size) ReadHeader(byte[] data, int off) =>
        (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off, 8)),
         BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8, 8)));

    [Fact]
    public async Task Encode_SingleFileDir_ProducesExpectedItemSequence()
    {
        var root = new PxarDirNode { Name = "", MtimeSecs = 1_700_000_000 };
        var content = Encoding.UTF8.GetBytes("hello");
        root.Children.Add(new PxarFileNode
        {
            Name = "a.txt",
            MtimeSecs = 1_700_000_000,
            Length = content.Length,
            OpenRead = () => new MemoryStream(content),
        });

        using var ms = new MemoryStream();
        await PxarEncoder.EncodeAsync(root, ms);
        var data = ms.ToArray();

        int off = 0;
        var (h0, s0) = ReadHeader(data, off); off += (int)s0;
        Assert.Equal(PxarFormat.FormatVersion, h0);
        Assert.Equal((ulong)(PxarFormat.HeaderSize + 8), s0);

        var (h1, _) = ReadHeader(data, off); off += PxarFormat.HeaderSize + PxarFormat.StatSize;
        Assert.Equal(PxarFormat.Entry, h1); // root dir entry

        var (h2, s2) = ReadHeader(data, off); off += (int)s2;
        Assert.Equal(PxarFormat.Filename, h2); // "a.txt\0"
        Assert.Equal((ulong)(PxarFormat.HeaderSize + 6), s2);

        var (h3, _) = ReadHeader(data, off); off += PxarFormat.HeaderSize + PxarFormat.StatSize;
        Assert.Equal(PxarFormat.Entry, h3); // file entry

        var (h4, s4) = ReadHeader(data, off); off += (int)s4;
        Assert.Equal(PxarFormat.Payload, h4);
        Assert.Equal((ulong)(PxarFormat.HeaderSize + content.Length), s4);

        var (h5, s5) = ReadHeader(data, off);
        Assert.Equal(PxarFormat.Goodbye, h5);
        // 1 child item + tail marker
        Assert.Equal((ulong)(PxarFormat.HeaderSize + 2 * PxarFormat.GoodbyeItemSize), s5);

        // The whole archive is exactly consumed.
        Assert.Equal(data.Length, off + (int)s5);
    }
}
