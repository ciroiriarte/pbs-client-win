using System.Text.Json;
using Pbs.Abstractions;
using Pbs.Chunker;
using Pbs.Protocol;
using Pbs.Pxar;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Encodes a real directory tree as pxar, backs it up as <c>root.pxar.didx</c>, then restores the
/// archive over the reader protocol and asserts the bytes survive PBS intact. (The archive's
/// validity as pxar is separately confirmed by restoring it with the official proxmox-backup-client.)
/// </summary>
public class PxarBackupTests
{
    private readonly ITestOutputHelper _out;
    public PxarBackupTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task EncodeDirectory_Backup_Restore_BytesMatch()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);

        var dir = Directory.CreateTempSubdirectory("pxar-it-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello world\n");
            File.WriteAllBytes(Path.Combine(dir, "empty.txt"), Array.Empty<byte>());
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var big = new byte[100_000];
            new Random(777).NextBytes(big);
            File.WriteAllBytes(Path.Combine(dir, "sub", "data.bin"), big);

            using var pxar = new MemoryStream();
            await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(dir), pxar);
            var pxarBytes = pxar.ToArray();
            _out.WriteLine($"pxar bytes = {pxarBytes.Length}");

            var chunks = new ContentDefinedChunker(
                new ChunkerOptions { MinSize = 32 * 1024, MaxSize = 256 * 1024, MaskBits = 16 }).Split(pxarBytes);

            const string archive = "root.pxar.didx";
            var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var backupId = $"pxar-it-{backupTime}";

            await using (var s = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime))
            {
                var wid = await s.CreateDynamicIndexAsync(archive);
                var seen = new HashSet<ChunkDigest>();
                foreach (var c in chunks)
                {
                    if (!seen.Add(c.Digest)) continue;
                    var blob = DataBlob.Encode(pxarBytes.AsSpan((int)c.Offset, c.Length).ToArray(), compress: true);
                    await s.UploadDynamicChunkAsync(wid, c, blob);
                }
                await s.AppendDynamicIndexAsync(wid, chunks);
                await s.CloseDynamicIndexAsync(archive, wid, chunks.Count, pxarBytes.Length, DynamicIndex.ComputeCsum(chunks));
                await s.FinishAsync();
            }

            // The manifest should list our archive.
            using var client = PbsTestServer.CreateClient();
            var snapshots = await client.ListSnapshotsAsync(backupId);
            var files = snapshots.SelectMany(s => s.GetProperty("files").EnumerateArray())
                                 .Select(f => f.GetProperty("filename").GetString());
            Assert.Contains("root.pxar.didx", files);

            // Restore the archive bytes and confirm they match what we uploaded.
            await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);
            using var restored = new MemoryStream();
            await reader.RestoreDynamicArchiveAsync(archive, restored);
            Assert.True(pxarBytes.AsSpan().SequenceEqual(restored.ToArray()), "restored pxar bytes differ from original");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
