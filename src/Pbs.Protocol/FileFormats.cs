namespace Pbs.Protocol;

/// <summary>
/// Proxmox Backup Server on-wire/on-disk magic numbers. Each is the first 8 bytes of the SHA-256
/// of a fixed ASCII label (see <c>Label</c>), exactly as upstream generates them in
/// <c>pbs-datastore/src/file_formats.rs</c>. The values are hard-coded for interop; the labels
/// are retained so a unit test can re-derive and assert each constant.
/// </summary>
public static class FileFormats
{
    public static readonly byte[] UncompressedBlobMagic = { 66, 171, 56, 7, 190, 131, 112, 161 };
    public static readonly byte[] CompressedBlobMagic = { 49, 185, 88, 66, 111, 182, 163, 127 };
    public static readonly byte[] EncryptedBlobMagic = { 123, 103, 133, 190, 34, 45, 76, 240 };
    public static readonly byte[] EncrComprBlobMagic = { 230, 89, 27, 191, 11, 191, 216, 11 };

    public static readonly byte[] FixedSizedChunkIndexMagic = { 47, 127, 65, 237, 145, 253, 15, 205 };
    public static readonly byte[] DynamicSizedChunkIndexMagic = { 28, 145, 78, 165, 25, 186, 179, 205 };

    public static readonly byte[] CatalogFileMagic = { 145, 253, 96, 249, 196, 103, 88, 213 };

    /// <summary>The ASCII labels upstream hashes to derive each magic (for verification/tests).</summary>
    public static class Label
    {
        public const string UncompressedBlob = "Proxmox Backup uncompressed blob v1.0";
        public const string CompressedBlob = "Proxmox Backup zstd compressed blob v1.0";
        public const string EncryptedBlob = "Proxmox Backup encrypted blob v1.0";
        public const string EncrComprBlob = "Proxmox Backup zstd compressed encrypted blob v1.0";
        public const string FixedSizedChunkIndex = "Proxmox Backup fixed sized chunk index v1.0";
        public const string DynamicSizedChunkIndex = "Proxmox Backup dynamic sized chunk index v1.0";
        public const string CatalogFile = "Proxmox Backup Catalog file v1.0";
    }

    /// <summary>The fixed on-disk index header size (one page), for both fixed and dynamic indexes.</summary>
    public const int IndexHeaderSize = 4096;
}
