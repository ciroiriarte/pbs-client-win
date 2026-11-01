namespace Pbs.Cli;

/// <summary>
/// Resolves a user-given pxar archive name against a snapshot's manifest, the way upstream's
/// <c>get_pxar_archive_names</c> does (issue #33). A file backup is stored either as one archive
/// (<c>root.pxar.didx</c>) or, since pxar v2, split into metadata + payload
/// (<c>root.mpxar.didx</c> + <c>root.ppxar.didx</c>). Users keep typing <c>root.pxar</c>; if that is
/// not in the manifest, the split pair is used instead.
/// </summary>
public static class PxarArchiveNames
{
    /// <summary>
    /// Returns the stored metadata (or unified) archive name and, for a split archive, the payload
    /// archive name. Throws when neither form is in <paramref name="manifestFiles"/>.
    /// </summary>
    public static (string Archive, string? Payload) Resolve(string name, IReadOnlyCollection<string> manifestFiles)
    {
        var stored = name.EndsWith(".didx", StringComparison.Ordinal) ? name
            : name.EndsWith(".pxar", StringComparison.Ordinal) || name.EndsWith(".mpxar", StringComparison.Ordinal) ||
              name.EndsWith(".ppxar", StringComparison.Ordinal) ? name + ".didx"
            : name + ".pxar.didx";
        var baseName = stored[..^".didx".Length];

        if (manifestFiles.Contains(stored))
        {
            var splitBase = StripSuffix(baseName, ".mpxar") ?? StripSuffix(baseName, ".ppxar");
            return splitBase is null ? (stored, null) : SplitPair(splitBase);
        }

        if (StripSuffix(baseName, ".pxar") is { } b && manifestFiles.Contains($"{b}.mpxar.didx"))
        {
            var pair = SplitPair(b);
            if (!manifestFiles.Contains(pair.Payload!))
                throw new InvalidDataException($"split archive '{pair.Archive}' has no payload archive '{pair.Payload}' in the manifest");
            return pair;
        }

        throw new ArgumentException($"archive '{name}' not found in snapshot (has: {string.Join(", ", manifestFiles)})");
    }

    /// <summary>Stored names for a new split archive with the given base (e.g. <c>root</c>).</summary>
    public static (string Archive, string? Payload) SplitPair(string baseName) =>
        ($"{baseName}.mpxar.didx", $"{baseName}.ppxar.didx");

    /// <summary>Archive base name from a user spec: <c>root.pxar</c> / <c>root.mpxar.didx</c> / <c>root</c> → <c>root</c>.</summary>
    public static string BaseName(string name)
    {
        var n = name.EndsWith(".didx", StringComparison.Ordinal) ? name[..^5] : name;
        return StripSuffix(n, ".pxar") ?? StripSuffix(n, ".mpxar") ?? StripSuffix(n, ".ppxar") ?? n;
    }

    private static string? StripSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : null;
}
