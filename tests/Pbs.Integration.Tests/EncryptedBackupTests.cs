using Pbs.Chunker;
using Pbs.Protocol;
using Pbs.Pxar;
using Xunit;
using Xunit.Abstractions;

namespace Pbs.Integration.Tests;

/// <summary>
/// Backs up an encrypted pxar archive and restores it over the reader protocol with the same key,
/// asserting a byte-exact round-trip. (Interop with the official client's <c>--keyfile</c> is
/// separately confirmed by restoring a client-encrypted backup with proxmox-backup-client.)
/// </summary>
public class EncryptedBackupTests
{
    // A real `key create --kdf none` key (see CryptConfigTests for the matching fingerprint).
    private const string KeyBase64 = "ty1PYdB2OfWc8IkaGVh/Rtvu2d/CFv7rQ231UVpV07I=";

    private readonly ITestOutputHelper _out;
    public EncryptedBackupTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public async Task EncryptedBackup_Restore_BytesMatch()
    {
        Skip.IfNot(PbsTestServer.Available, PbsTestServer.SkipReason);
        var crypt = new CryptConfig(Convert.FromBase64String(KeyBase64));

        var dir = Directory.CreateTempSubdirectory("pxar-enc-it-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "secret.txt"), "top secret\n");
            var big = new byte[80_000];
            new Random(555).NextBytes(big);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), big);

            using var pxar = new MemoryStream();
            await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(dir), pxar);
            var pxarBytes = pxar.ToArray();

            var chunks = new ContentDefinedChunker(
                new ChunkerOptions { MinSize = 16 * 1024, MaxSize = 128 * 1024, MaskBits = 15 }).Split(pxarBytes);

            const string archive = "root.pxar.didx";
            var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var backupId = $"enc-it-{backupTime}";

            await using (var s = await BackupSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime))
            {
                s.SetKeyFingerprint(crypt.FingerprintHex());
                var wid = await s.CreateDynamicIndexAsync(archive);
                var indexed = chunks
                    .Select(c => c with { Digest = Pbs.Abstractions.ChunkDigest.FromBytes(crypt.ComputeDigest(pxarBytes.AsSpan((int)c.Offset, c.Length))) })
                    .ToList();
                var seen = new HashSet<Pbs.Abstractions.ChunkDigest>();
                foreach (var c in indexed)
                {
                    if (!seen.Add(c.Digest)) continue;
                    var blob = crypt.EncryptBlob(pxarBytes.AsSpan((int)c.Offset, c.Length).ToArray());
                    await s.UploadDynamicChunkAsync(wid, c, blob);
                }
                await s.AppendDynamicIndexAsync(wid, indexed);
                await s.CloseDynamicIndexAsync(archive, wid, indexed.Count, pxarBytes.Length, DynamicIndex.ComputeCsum(indexed), "encrypt");
                await s.FinishAsync();
            }

            await using var reader = await ReaderSession.OpenAsync(PbsTestServer.Options(), "host", backupId, backupTime);
            using var restored = new MemoryStream();
            await reader.RestoreDynamicArchiveAsync(archive, restored, crypt);
            _out.WriteLine($"pxar={pxarBytes.Length} restored={restored.Length}");
            Assert.True(pxarBytes.AsSpan().SequenceEqual(restored.ToArray()), "decrypted bytes differ from original");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
