using System.Reflection;
using Pbs.Abstractions;
using Pbs.Cli;
using Pbs.Protocol;
using Pbs.Pxar;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        PrintUsage(version);
        return 0;
    }

    var (positionals, flags, multi) = ParseArgs(args[1..]);
    switch (args[0])
    {
        case "version" or "--version":
            Console.WriteLine(version);
            return 0;
        case "backup":
            return await BackupAsync(positionals, flags, multi);
        case "restore":
            return await RestoreAsync(positionals, flags);
        case "snapshot" when positionals is ["list", ..]:
        case "snapshots": // alias
            return await SnapshotsAsync(flags);
        case "snapshot" when positionals is ["forget", var snap, ..]:
            return await ForgetAsync(snap, flags);
        case "snapshot" when positionals is ["notes", var snap, ..]:
            return await NotesAsync(snap, positionals.Skip(2).ToList(), flags);
        case "snapshot":
            Console.Error.WriteLine("usage: pbs-client-win snapshot list|forget <type/id/time>|notes <type/id/time> [text]");
            return 1;
        case "prune":
            return await PruneAsync(positionals, flags);
        case "namespace" when positionals is ["create", var ns, ..]:
            return await NamespaceCreateAsync(ns, flags);
        case "namespace":
            Console.Error.WriteLine("usage: pbs-client-win namespace create <namespace> --repository <repo>");
            return 1;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            Console.Error.WriteLine("run 'pbs-client-win --help' for usage");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// ---- commands ----

static async Task<int> BackupAsync(List<string> specs, Dictionary<string, string> flags, ILookup<string, string> multi)
{
    if (specs.Count == 0) { Console.Error.WriteLine("backup: expected at least one <name.pxar>:<dir>"); return 1; }

    var opts = ResolveConnection(flags);
    var backupType = flags.GetValueOrDefault("backup-type", "host");
    var backupId = flags.GetValueOrDefault("backup-id", Environment.MachineName.ToLowerInvariant());
    var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var crypt = LoadCrypt(flags);
    var imageMode = flags.ContainsKey("image");   // back up whole volumes as fixed images (FIDX)
    var useVss = flags.ContainsKey("vss") || imageMode; // image backup always needs a snapshot
    var incremental = !flags.ContainsKey("no-incremental");
    var excludes = new PxarExcludes(multi["exclude"]); // --exclude glob patterns (file backup only)
    if (!excludes.IsEmpty) Console.WriteLine($"  excludes: {string.Join(", ", multi["exclude"])}");

    var items = specs.Select(spec =>
    {
        int c = spec.IndexOf(':');
        if (c <= 0) throw new ArgumentException($"invalid archive spec '{spec}' (expected name:target)");
        return (Name: spec[..c], Target: spec[(c + 1)..]);
    }).ToList();

    // The volumes to snapshot: the volume itself in image mode, else each directory's volume root.
    var volumes = imageMode
        ? items.Select(i => i.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : items.Select(i => Path.GetPathRoot(Path.GetFullPath(i.Target)) ?? throw new ArgumentException($"no volume root for '{i.Target}'"))
               .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    using ISnapshotSource snapshot = CreateSnapshotSource(useVss, volumes);
    if (useVss) Console.WriteLine($"  VSS: snapshotting {string.Join(", ", volumes)}");

    await using var session = await BackupSession.OpenAsync(opts, backupType, backupId, backupTime);
    if (crypt is not null) { session.SetKeyFingerprint(crypt.FingerprintHex()); Console.WriteLine($"  encryption: on (key {crypt.FingerprintHex()[..17]}…)"); }
    foreach (var (name, target) in items)
    {
        DynamicArchive.UploadStats stats;
        string stored;
        if (imageMode)
        {
            stored = StoredImageName(name);
            var (stream, size) = snapshot.OpenImage(target);
            Console.WriteLine($"  {name}: {target} -> {stored} ({size} bytes, fixed image)");
            stats = await FixedImage.UploadAsync(session, stored, stream, size, crypt: crypt, incremental: incremental);
        }
        else
        {
            var path = Path.GetFullPath(target);
            if (!Directory.Exists(path)) throw new ArgumentException($"'{path}' is not a directory (use --image to back up a volume)");
            stored = StoredArchiveName(name);
            var readPath = snapshot.Map(path); // live path, or its shadow-copy path under VSS
            using var pxar = new MemoryStream();
            await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(readPath, excludes), pxar);
            Console.WriteLine($"  {name}: {path} -> {stored} ({pxar.Length} bytes)");
            stats = await DynamicArchive.UploadAsync(session, stored, pxar.ToArray(), crypt: crypt, incremental: incremental);
        }
        Console.WriteLine($"    chunks: {stats.TotalChunks} total, {stats.UploadedChunks} uploaded, {stats.ReusedChunks} reused");
    }
    snapshot.Complete();
    await session.FinishAsync();
    Console.WriteLine($"backup complete: {backupType}/{backupId}/{backupTime}");
    return 0;
}

static async Task<int> RestoreAsync(List<string> positionals, Dictionary<string, string> flags)
{
    if (positionals.Count != 3) { Console.Error.WriteLine("restore: expected <snapshot> <archive.pxar> <target-dir>"); return 1; }
    var (snapshot, archive, target) = (positionals[0], positionals[1], positionals[2]);

    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(snapshot);
    var crypt = LoadCrypt(flags);

    await using var reader = await ReaderSession.OpenAsync(opts, type, id, time);
    if (flags.ContainsKey("image"))
    {
        // Restore a fixed image straight to a raw file.
        var storedImg = StoredImageName(archive);
        await using var file = File.Create(target);
        var written = await reader.RestoreFixedArchiveAsync(storedImg, file, crypt);
        Console.WriteLine($"restored {storedImg} -> {target} ({written} bytes, raw image)");
        return 0;
    }

    var stored = StoredArchiveName(archive);
    using var buffer = new MemoryStream();
    await reader.RestoreDynamicArchiveAsync(stored, buffer, crypt);
    buffer.Position = 0;
    await PxarDecoder.ExtractAsync(buffer, target);
    Console.WriteLine($"restored {stored} -> {target} ({buffer.Length} bytes)");
    return 0;
}

static async Task<int> SnapshotsAsync(Dictionary<string, string> flags)
{
    var opts = ResolveConnection(flags);
    using var client = PbsClient.Create(opts);
    var snapshots = await client.ListSnapshotsAsync();
    if (flags.GetValueOrDefault("output-format") == "json")
    {
        Console.WriteLine("[" + string.Join(",", snapshots.Select(s => s.GetRawText())) + "]");
        return 0;
    }
    foreach (var s in snapshots)
    {
        var t = s.GetProperty("backup-type").GetString();
        var id = s.GetProperty("backup-id").GetString();
        var time = s.GetProperty("backup-time").GetInt64();
        Console.WriteLine($"{t}/{id}/{time}");
    }
    Console.WriteLine($"({snapshots.Count} snapshot(s))");
    return 0;
}

static async Task<int> ForgetAsync(string snapshot, Dictionary<string, string> flags)
{
    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(snapshot);
    using var client = PbsClient.Create(opts);
    await client.ForgetSnapshotAsync(type, id, time);
    Console.WriteLine($"forgot {type}/{id}/{time}");
    return 0;
}

static async Task<int> NotesAsync(string snapshot, List<string> rest, Dictionary<string, string> flags)
{
    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(snapshot);
    using var client = PbsClient.Create(opts);
    if (rest.Count == 0)
    {
        Console.WriteLine(await client.GetNotesAsync(type, id, time));
    }
    else
    {
        await client.SetNotesAsync(type, id, time, string.Join(' ', rest));
        Console.WriteLine("notes updated");
    }
    return 0;
}

static async Task<int> PruneAsync(List<string> positionals, Dictionary<string, string> flags)
{
    if (positionals.Count != 1) { Console.Error.WriteLine("prune: expected a group <type/id>"); return 1; }
    var parts = positionals[0].Split('/');
    if (parts.Length != 2) throw new ArgumentException($"invalid group '{positionals[0]}' (expected type/id)");

    var keep = new Dictionary<string, long>();
    foreach (var opt in new[] { "keep-last", "keep-hourly", "keep-daily", "keep-weekly", "keep-monthly", "keep-yearly" })
        if (flags.TryGetValue(opt, out var v) && long.TryParse(v, out var n)) keep[opt] = n;
    if (keep.Count == 0) { Console.Error.WriteLine("prune: specify at least one --keep-* option"); return 1; }

    var dryRun = flags.ContainsKey("dry-run");
    var opts = ResolveConnection(flags);
    using var client = PbsClient.Create(opts);
    var entries = await client.PruneGroupAsync(parts[0], parts[1], keep, dryRun);
    foreach (var e in entries.OrderByDescending(e => e.BackupTime))
        Console.WriteLine($"  {(e.Keep ? "keep  " : "remove")} {parts[0]}/{parts[1]}/{e.BackupTime}");
    Console.WriteLine($"{(dryRun ? "[dry-run] " : "")}{entries.Count(e => !e.Keep)} removed, {entries.Count(e => e.Keep)} kept");
    return 0;
}

static async Task<int> NamespaceCreateAsync(string ns, Dictionary<string, string> flags)
{
    var opts = ResolveConnection(flags);
    using var client = PbsClient.Create(opts);
    await client.CreateNamespaceAsync(ns);
    Console.WriteLine($"created namespace {ns}");
    return 0;
}

// ---- helpers ----

static PbsConnectionOptions ResolveConnection(Dictionary<string, string> flags)
{
    var repoSpec = flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY")
        ?? throw new ArgumentException("no repository (use --repository or PBS_REPOSITORY)");
    var repo = PbsRepository.Parse(repoSpec);
    if (repo.AuthId is null)
        throw new ArgumentException("repository must include an API token id (user@realm!token@host:datastore)");

    var secret = flags.GetValueOrDefault("password") ?? Environment.GetEnvironmentVariable("PBS_PASSWORD")
        ?? throw new ArgumentException("no token secret (use --password or PBS_PASSWORD)");
    var fp = flags.GetValueOrDefault("fingerprint") ?? Environment.GetEnvironmentVariable("PBS_FINGERPRINT");
    var ns = flags.GetValueOrDefault("ns") ?? Environment.GetEnvironmentVariable("PBS_NAMESPACE");

    return new PbsConnectionOptions
    {
        BaseUrl = repo.BaseUrl,
        Token = new PbsApiToken(repo.AuthId, secret),
        Fingerprint = string.IsNullOrEmpty(fp) ? null : CertificateFingerprint.Parse(fp),
        Datastore = repo.Datastore,
        Namespace = ns,
    };
}

static ISnapshotSource CreateSnapshotSource(bool useVss, IReadOnlyCollection<string> volumes)
{
    if (!useVss) return new IdentitySnapshotSource();
#if WINDOWS
    return new Pbs.Vss.VssSnapshotSource(volumes);
#else
    throw new PlatformNotSupportedException("--vss requires the Windows build of pbs-client-win");
#endif
}

static CryptConfig? LoadCrypt(Dictionary<string, string> flags)
{
    var keyfile = flags.GetValueOrDefault("keyfile") ?? Environment.GetEnvironmentVariable("PBS_KEYFILE");
    if (string.IsNullOrEmpty(keyfile)) return null;
    return new CryptConfig(KeyFile.LoadRawKey(keyfile));
}

static string StoredArchiveName(string name) =>
    name.EndsWith(".didx") ? name
    : name.EndsWith(".pxar") ? name + ".didx"
    : name + ".pxar.didx";

static string StoredImageName(string name) =>
    name.EndsWith(".fidx") ? name
    : name.EndsWith(".img") ? name + ".fidx"
    : name + ".img.fidx";

static (string type, string id, long time) ParseSnapshot(string snapshot)
{
    var parts = snapshot.Split('/');
    if (parts.Length != 3) throw new ArgumentException($"invalid snapshot '{snapshot}' (expected type/id/time)");
    long time = long.TryParse(parts[2], out var epoch)
        ? epoch
        : DateTimeOffset.Parse(parts[2], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeSeconds();
    return (parts[0], parts[1], time);
}

static (List<string> positionals, Dictionary<string, string> flags, ILookup<string, string> multi) ParseArgs(string[] args)
{
    var positionals = new List<string>();
    var pairs = new List<KeyValuePair<string, string>>();
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            var key = a[2..];
            string val;
            int eq = key.IndexOf('=');
            if (eq >= 0) { val = key[(eq + 1)..]; key = key[..eq]; }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { val = args[++i]; }
            else { val = "true"; }
            pairs.Add(new KeyValuePair<string, string>(key, val));
        }
        else positionals.Add(a);
    }
    // flags = last value wins per key; multi = all values per key (for repeatable flags like --exclude)
    var flags = pairs.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
    var multi = pairs.ToLookup(p => p.Key, p => p.Value, StringComparer.Ordinal);
    return (positionals, flags, multi);
}

static void PrintUsage(string version)
{
    Console.WriteLine("pbs-client-win — Proxmox Backup client for Windows");
    Console.WriteLine($"version {version}");
    Console.WriteLine();
    Console.WriteLine("Usage (verbs/flags mirror the upstream proxmox-backup-client where possible):");
    Console.WriteLine("  pbs-client-win backup <name.pxar>:<dir> [...] --repository <repo> [--ns <ns>] [--backup-id <id>] [--vss] [--no-incremental]");
    Console.WriteLine("  pbs-client-win backup <name.img>:<volume> --image --repository <repo> [--ns <ns>]   (Windows; whole-volume image via VSS)");
    Console.WriteLine("  pbs-client-win restore <type/id/time> <name.pxar> <target-dir> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win snapshot list --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win snapshot forget <type/id/time> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win snapshot notes <type/id/time> [text] --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win prune <type/id> --keep-last N [--keep-daily N ...] [--dry-run] --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win namespace create <namespace> --repository <repo>");
    Console.WriteLine();
    Console.WriteLine("backup file mode also accepts repeatable --exclude <glob> (e.g. --exclude *.tmp --exclude /logs/**).");
    Console.WriteLine("snapshot list accepts --output-format json.");
    Console.WriteLine();
    Console.WriteLine("Repository: [AUTHID@]HOST[:PORT]:DATASTORE  (e.g. root@pam!tok@pbs.example:store01)");
    Console.WriteLine("Credentials: --password/PBS_PASSWORD (token secret), --fingerprint/PBS_FINGERPRINT.");
    Console.WriteLine("Encryption: --keyfile/PBS_KEYFILE (a 'key create --kdf none' keyfile); AES-256-GCM,");
    Console.WriteLine("            interoperable with the official client's --keyfile.");
    Console.WriteLine();
    Console.WriteLine("This is an independent, community project. It is NOT produced, endorsed by, or");
    Console.WriteLine("affiliated with Proxmox Server Solutions GmbH. \"Proxmox\" is their trademark.");
}
