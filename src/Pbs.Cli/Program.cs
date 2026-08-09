using System.Reflection;
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

    var (positionals, flags) = ParseArgs(args[1..]);
    switch (args[0])
    {
        case "version" or "--version":
            Console.WriteLine(version);
            return 0;
        case "backup":
            return await BackupAsync(positionals, flags);
        case "restore":
            return await RestoreAsync(positionals, flags);
        case "snapshots":
            return await SnapshotsAsync(flags);
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

static async Task<int> BackupAsync(List<string> specs, Dictionary<string, string> flags)
{
    if (specs.Count == 0) { Console.Error.WriteLine("backup: expected at least one <name.pxar>:<dir>"); return 1; }

    var opts = ResolveConnection(flags);
    var backupType = flags.GetValueOrDefault("backup-type", "host");
    var backupId = flags.GetValueOrDefault("backup-id", Environment.MachineName.ToLowerInvariant());
    var backupTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var crypt = LoadCrypt(flags);

    await using var session = await BackupSession.OpenAsync(opts, backupType, backupId, backupTime);
    if (crypt is not null) { session.SetKeyFingerprint(crypt.FingerprintHex()); Console.WriteLine($"  encryption: on (key {crypt.FingerprintHex()[..17]}…)"); }
    foreach (var spec in specs)
    {
        int c = spec.IndexOf(':');
        if (c <= 0) throw new ArgumentException($"invalid archive spec '{spec}' (expected name.pxar:/path)");
        var name = spec[..c];
        var path = spec[(c + 1)..];
        if (!Directory.Exists(path)) throw new ArgumentException($"'{path}' is not a directory (only directory pxar backup is supported)");

        var stored = StoredArchiveName(name);
        using var pxar = new MemoryStream();
        await PxarEncoder.EncodeAsync(PxarTree.FromDirectory(path), pxar);
        Console.WriteLine($"  {name}: {path} -> {stored} ({pxar.Length} bytes)");
        await DynamicArchive.UploadAsync(session, stored, pxar.ToArray(), crypt: crypt);
    }
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
    var stored = StoredArchiveName(archive);
    var crypt = LoadCrypt(flags);

    await using var reader = await ReaderSession.OpenAsync(opts, type, id, time);
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

static (string type, string id, long time) ParseSnapshot(string snapshot)
{
    var parts = snapshot.Split('/');
    if (parts.Length != 3) throw new ArgumentException($"invalid snapshot '{snapshot}' (expected type/id/time)");
    long time = long.TryParse(parts[2], out var epoch)
        ? epoch
        : DateTimeOffset.Parse(parts[2], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeSeconds();
    return (parts[0], parts[1], time);
}

static (List<string> positionals, Dictionary<string, string> flags) ParseArgs(string[] args)
{
    var positionals = new List<string>();
    var flags = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a.StartsWith("--"))
        {
            var key = a[2..];
            int eq = key.IndexOf('=');
            if (eq >= 0) { flags[key[..eq]] = key[(eq + 1)..]; }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { flags[key] = args[++i]; }
            else { flags[key] = "true"; }
        }
        else positionals.Add(a);
    }
    return (positionals, flags);
}

static void PrintUsage(string version)
{
    Console.WriteLine("pbs-client-win — Proxmox Backup client for Windows");
    Console.WriteLine($"version {version}");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  pbs-client-win backup <name.pxar>:<dir> [...] --repository <repo> [--ns <ns>] [--backup-id <id>]");
    Console.WriteLine("  pbs-client-win restore <type/id/time> <name.pxar> <target-dir> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  pbs-client-win snapshots --repository <repo> [--ns <ns>]");
    Console.WriteLine();
    Console.WriteLine("Repository: [AUTHID@]HOST[:PORT]:DATASTORE  (e.g. root@pam!tok@pbs.example:store01)");
    Console.WriteLine("Credentials: --password/PBS_PASSWORD (token secret), --fingerprint/PBS_FINGERPRINT.");
    Console.WriteLine("Encryption: --keyfile/PBS_KEYFILE (a 'key create --kdf none' keyfile); AES-256-GCM,");
    Console.WriteLine("            interoperable with the official client's --keyfile.");
}
