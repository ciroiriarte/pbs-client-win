using System.Reflection;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Pbs.Abstractions;
using Pbs.Cli;
using Pbs.Nbd;
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
        case "benchmark":
            return await Benchmark.RunAsync(flags);
        case "login":
            return await LoginAsync(positionals, flags);
        case "logout":
            return Logout(positionals, flags);
        case "restore":
            return await RestoreAsync(positionals, flags, multi);
        case "map":
            return await MapAsync(positionals, flags);
        case "unmap":
            return await UnmapAsync(positionals, flags);
        case "snapshot" when positionals is ["list", ..]:
        case "snapshots": // alias
            return await SnapshotsAsync(flags);
        case "snapshot" when positionals is ["forget", var snap, ..]:
            return await ForgetAsync(snap, flags);
        case "snapshot" when positionals is ["notes", var snap, ..]:
            return await NotesAsync(snap, positionals.Skip(2).ToList(), flags);
        case "snapshot" when positionals is ["protected", var snap, var val, ..]:
            return await ProtectedAsync(snap, val, flags);
        case "snapshot":
            Console.Error.WriteLine("usage: proxmox-backup-client snapshot list|forget <type/id/time>|notes <type/id/time> [text]");
            return 1;
        case "change-owner":
            return await ChangeOwnerAsync(positionals, flags);
        case "prune":
            return await PruneAsync(positionals, flags);
        case "task" when positionals is ["list", ..]:
            return await TaskListAsync(flags);
        case "task" when positionals is ["log", var upid, ..]:
            return await TaskLogAsync(upid, flags);
        case "task" when positionals is ["stop", var upid, ..]:
            return await TaskStopAsync(upid, flags);
        case "task":
            Console.Error.WriteLine("usage: proxmox-backup-client task list|log <upid>|stop <upid> --repository <repo> [--node <node>]");
            return 1;
        case "namespace" when positionals is ["create", var ns, ..]:
            return await NamespaceCreateAsync(ns, flags);
        case "namespace" when positionals is ["list", ..]:
            return await NamespaceListAsync(flags);
        case "namespace" when positionals is ["delete", var ns, ..]:
            return await NamespaceDeleteAsync(ns, flags);
        case "namespace":
            Console.Error.WriteLine("usage: proxmox-backup-client namespace create|list|delete <namespace> --repository <repo>");
            return 1;
        case "status":
            return await StatusAsync(flags);
        case "catalog" when positionals is ["dump", var snap, ..]:
            return await CatalogDumpAsync(snap, flags);
        case "catalog":
            Console.Error.WriteLine("usage: proxmox-backup-client catalog dump <type/id/time> --repository <repo> [--ns <ns>]");
            return 1;
        case "key" when positionals is ["create", var keyPath, ..]:
            return KeyCreate(keyPath, flags);
        case "key" when positionals is ["change-passphrase", var keyPath, ..]:
            return KeyChangePassphrase(keyPath, flags);
        case "key" when positionals is ["create-master-key", ..]:
            return KeyCreateMasterKey(positionals.Skip(1).ToList(), flags);
        case "key" when positionals is ["import-master-pubkey", var pemPath, ..]:
            return KeyImportMasterPubkey(pemPath);
        case "key" when positionals is ["show-master-pubkey", ..]:
            return KeyShowMasterPubkey(positionals.Skip(1).ToList());
        case "key" when positionals is ["paperkey", ..]:
            return KeyPaperkey(positionals.Skip(1).ToList(), flags);
        case "key":
            Console.Error.WriteLine("usage: proxmox-backup-client key create|change-passphrase|create-master-key|import-master-pubkey|show-master-pubkey|paperkey ...");
            return 1;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            Console.Error.WriteLine("run 'proxmox-backup-client --help' for usage");
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
    if (crypt is not null) { session.SetKeyFingerprint(crypt.Config.FingerprintHex()); Console.WriteLine($"  encryption: on (key {crypt.Config.FingerprintHex()[..17]}…)"); }
    var catalogRoots = new List<(string Stored, PxarDirNode Tree)>(); // for the .pcat1 catalog (file mode)
    foreach (var (name, target) in items)
    {
        DynamicArchive.UploadStats stats;
        string stored;
        if (imageMode)
        {
            stored = StoredImageName(name);
            var (stream, size) = snapshot.OpenImage(target);
            Console.WriteLine($"  {name}: {target} -> {stored} ({size} bytes, fixed image)");
            stats = await FixedImage.UploadAsync(session, stored, stream, size, crypt: crypt?.Config, incremental: incremental);
        }
        else
        {
            var path = Path.GetFullPath(target);
            if (!Directory.Exists(path)) throw new ArgumentException($"'{path}' is not a directory (use --image to back up a volume)");
            stored = StoredArchiveName(name);
            var readPath = snapshot.Map(path); // live path, or its shadow-copy path under VSS
            var tree = PxarTree.FromDirectory(readPath, excludes);
            catalogRoots.Add((stored, tree));
            using var pxar = new MemoryStream();
            await PxarEncoder.EncodeAsync(tree, pxar);
            Console.WriteLine($"  {name}: {path} -> {stored} ({pxar.Length} bytes)");
            stats = await DynamicArchive.UploadAsync(session, stored, pxar.ToArray(), crypt: crypt?.Config, incremental: incremental);
        }
        Console.WriteLine($"    chunks: {stats.TotalChunks} total, {stats.UploadedChunks} uploaded, {stats.ReusedChunks} reused");
    }
    // Upload the catalog (.pcat1) for fast file browsing — each archive wrapped under its stored
    // name (e.g. root.pxar.didx), matching the official client.
    if (!imageMode && catalogRoots.Count > 0)
    {
        var forest = new PxarDirNode { Name = string.Empty };
        foreach (var (stored, tree) in catalogRoots)
        {
            var archiveDir = new PxarDirNode { Name = stored, MtimeSecs = tree.MtimeSecs, MtimeNanos = tree.MtimeNanos };
            archiveDir.Children.AddRange(tree.Children);
            forest.Children.Add(archiveDir);
        }
        var catalogBytes = PxarCatalog.Build(forest);
        var catStats = await DynamicArchive.UploadAsync(session, "catalog.pcat1.didx", catalogBytes, crypt: crypt?.Config, incremental: incremental);
        Console.WriteLine($"  catalog.pcat1.didx: {catalogBytes.Length} bytes ({catStats.UploadedChunks} uploaded)");
    }

    if (flags.TryGetValue("master-pubkey-file", out var masterPubkeyFile))
    {
        if (crypt is null) throw new ArgumentException("--master-pubkey-file requires --keyfile/PBS_KEYFILE");
        var unprotected = KeyFile.UnprotectedKeyfileJson(crypt.RawKey, crypt.Metadata?.Created, crypt.Metadata?.Modified);
        var blob = MasterKey.BuildRsaEncryptedKeyBlob(File.ReadAllText(masterPubkeyFile), unprotected);
        await session.UploadBlobAsync("rsa-encrypted.key.blob", blob);
        Console.WriteLine("uploaded rsa-encrypted.key.blob");
    }

    snapshot.Complete();
    await session.FinishAsync();
    Console.WriteLine($"backup complete: {backupType}/{backupId}/{backupTime}");
    return 0;
}

static async Task<int> RestoreAsync(List<string> positionals, Dictionary<string, string> flags, ILookup<string, string> multi)
{
    if (positionals.Count != 3) { Console.Error.WriteLine("restore: expected <snapshot> <archive.pxar> <target-dir>"); return 1; }
    var (snapshot, archive, target) = (positionals[0], positionals[1], positionals[2]);

    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(snapshot);
    var crypt = LoadCrypt(flags)?.Config;

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

    var patterns = multi["pattern"].ToList();
    if (patterns.Count > 0) Console.WriteLine($"  patterns: {string.Join(", ", patterns)}");

    var stored = StoredArchiveName(archive);
    using var buffer = new MemoryStream();
    await reader.RestoreDynamicArchiveAsync(stored, buffer, crypt);
    buffer.Position = 0;
    await PxarDecoder.ExtractAsync(buffer, target, patterns);
    Console.WriteLine($"restored {stored} -> {target} ({buffer.Length} bytes)");
    return 0;
}

static async Task<int> MapAsync(List<string> positionals, Dictionary<string, string> flags)
{
    if (positionals.Count != 2) { Console.Error.WriteLine("map: expected <snapshot> <archive>"); return 1; }

    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(positionals[0]);
    var stored = StoredImageName(positionals[1]);
    var crypt = LoadCrypt(flags)?.Config;
    var bind = IPAddress.Parse(flags.GetValueOrDefault("bind", "127.0.0.1"));
    var port = int.Parse(flags.GetValueOrDefault("port", "10809"));
    var exportName = flags.GetValueOrDefault("export-name", stored);
    var device = flags.GetValueOrDefault("device");

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler? cancelHandler = (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    string? mappedDevice = null;
    try
    {
        await using var reader = await ReaderSession.OpenAsync(opts, type, id, time, cts.Token);
        var size = await reader.GetArchiveSizeAsync(stored, crypt, cts.Token);
        var source = new ReaderSessionBlockSource(reader, stored, crypt, size);
        var server = new NbdServer(source, exportName);
        var listenTask = server.ListenAsync(bind, port, cts.Token);

        while (server.LocalEndPoint is null && !listenTask.IsCompleted)
            await Task.Delay(25, cts.Token);
        if (listenTask.IsFaulted)
            await listenTask;

        var endpoint = server.LocalEndPoint ?? throw new InvalidOperationException("NBD listener did not bind.");
        Console.WriteLine($"NBD export '{exportName}' serving {stored} ({size} bytes) at {endpoint.Address}:{endpoint.Port} read-only");

        if (!string.IsNullOrWhiteSpace(device))
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine("wnbd-client attach is Windows-only; server will keep serving.");
                Console.WriteLine($"Run on Windows: wnbd-client map {device} {endpoint.Address} --port {endpoint.Port} --export-name {exportName}");
            }
            else if (FindOnPath("wnbd-client.exe") is null && FindOnPath("wnbd-client") is null)
            {
                Console.WriteLine("wnbd-client was not found on PATH; server will keep serving.");
                Console.WriteLine($"Run manually: wnbd-client map {device} {endpoint.Address} --port {endpoint.Port} --export-name {exportName}");
            }
            else
            {
                var exit = await RunProcessAsync("wnbd-client", $"map {QuoteArg(device)} {endpoint.Address} --port {endpoint.Port} --export-name {QuoteArg(exportName)}", cts.Token);
                if (exit != 0)
                    return exit;
                mappedDevice = device;
                Console.WriteLine($"mapped {device}");
            }
        }

        Console.WriteLine("Press Ctrl-C to stop serving.");
        await listenTask;
        return 0;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        if (!string.IsNullOrWhiteSpace(mappedDevice) && OperatingSystem.IsWindows())
            await RunProcessAsync("wnbd-client", $"unmap {QuoteArg(mappedDevice)}", CancellationToken.None);
    }
}

static async Task<int> UnmapAsync(List<string> positionals, Dictionary<string, string> flags)
{
    _ = flags;
    if (positionals.Count != 1) { Console.Error.WriteLine("unmap: expected <instance>"); return 1; }
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("unmap is Windows-only because it invokes wnbd-client.");
        return 1;
    }
    return await RunProcessAsync("wnbd-client", $"unmap {QuoteArg(positionals[0])}", CancellationToken.None);
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

static async Task<int> ChangeOwnerAsync(List<string> positionals, Dictionary<string, string> flags)
{
    if (positionals.Count != 2) { Console.Error.WriteLine("change-owner: expected <type/id> <new-owner>"); return 1; }

    var group = positionals[0].Split('/', 2);
    if (group.Length != 2) { Console.Error.WriteLine("change-owner: group must be <type/id>, e.g. host/my-host"); return 1; }

    var opts = ResolveConnection(flags);
    using var client = PbsClient.Create(opts);
    await client.ChangeOwnerAsync(group[0], group[1], positionals[1]);
    Console.WriteLine($"changed owner of {positionals[0]} to {positionals[1]}");
    return 0;
}

static async Task<int> TaskListAsync(Dictionary<string, string> flags)
{
    var node = flags.GetValueOrDefault("node", "localhost");
    var running = flags.ContainsKey("running");
    var limit = int.TryParse(flags.GetValueOrDefault("limit"), out var parsedLimit) ? parsedLimit : 50;

    using var client = PbsClient.Create(ResolveConnection(flags));
    var tasks = await client.ListTasksAsync(node, running, limit);
    if (flags.GetValueOrDefault("output-format") == "json")
    {
        Console.WriteLine("[" + string.Join(",", tasks.Select(t => t.GetRawText())) + "]");
        return 0;
    }

    foreach (var task in tasks)
    {
        var upid = GetString(task, "upid") ?? GetString(task, "UPID") ?? "-";
        var status = GetString(task, "status") ?? (task.TryGetProperty("endtime", out _) ? "-" : "running");
        var starttime = GetInt64(task, "starttime")?.ToString() ?? "-";
        Console.WriteLine($"{upid}  {status}  {starttime}");
    }
    return 0;
}

static async Task<int> TaskLogAsync(string upid, Dictionary<string, string> flags)
{
    var node = flags.GetValueOrDefault("node", "localhost");
    using var client = PbsClient.Create(ResolveConnection(flags));
    Console.WriteLine(await client.GetTaskLogAsync(node, upid));
    return 0;
}

static async Task<int> TaskStopAsync(string upid, Dictionary<string, string> flags)
{
    var node = flags.GetValueOrDefault("node", "localhost");
    using var client = PbsClient.Create(ResolveConnection(flags));
    await client.StopTaskAsync(node, upid);
    Console.WriteLine($"stopped task {upid}");
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

static async Task<int> NamespaceListAsync(Dictionary<string, string> flags)
{
    using var client = PbsClient.Create(ResolveConnection(flags));
    foreach (var ns in await client.ListNamespacesAsync())
        Console.WriteLine(ns.Length == 0 ? "(root)" : ns);
    return 0;
}

static async Task<int> NamespaceDeleteAsync(string ns, Dictionary<string, string> flags)
{
    using var client = PbsClient.Create(ResolveConnection(flags));
    await client.DeleteNamespaceAsync(ns, flags.ContainsKey("delete-groups"));
    Console.WriteLine($"deleted namespace {ns}");
    return 0;
}

static async Task<int> ProtectedAsync(string snapshot, string value, Dictionary<string, string> flags)
{
    var (type, id, time) = ParseSnapshot(snapshot);
    using var client = PbsClient.Create(ResolveConnection(flags));
    var on = value is "true" or "1" or "on";
    await client.SetProtectedAsync(type, id, time, on);
    Console.WriteLine($"{type}/{id}/{time} protected={on}");
    return 0;
}

static async Task<int> CatalogDumpAsync(string snapshot, Dictionary<string, string> flags)
{
    var opts = ResolveConnection(flags);
    var (type, id, time) = ParseSnapshot(snapshot);
    var crypt = LoadCrypt(flags)?.Config;

    await using var reader = await ReaderSession.OpenAsync(opts, type, id, time);
    using var buffer = new MemoryStream();
    await reader.RestoreDynamicArchiveAsync("catalog.pcat1.didx", buffer, crypt);

    var root = PxarCatalog.Parse(buffer.ToArray());
    foreach (var item in PxarCatalog.Enumerate(root))
        Console.WriteLine($"{item.Path}\t{(item.Size?.ToString() ?? "-")}");
    return 0;
}

static int KeyCreate(string path, Dictionary<string, string> flags)
{
    var kdf = flags.GetValueOrDefault("kdf", "scrypt");
    string? passphrase = null;
    if (kdf == "scrypt")
        passphrase = flags.GetValueOrDefault("key-password") ?? Environment.GetEnvironmentVariable("PBS_ENCRYPTION_PASSWORD")
            ?? throw new ArgumentException("scrypt key requires --key-password or PBS_ENCRYPTION_PASSWORD (or use --kdf none)");
    else if (kdf != "none")
        throw new ArgumentException("--kdf must be 'none' or 'scrypt'");

    var fingerprint = KeyFile.Create(path, passphrase);
    Console.WriteLine($"created key {path} (kdf {kdf}, fingerprint {fingerprint})");
    return 0;
}

static int KeyChangePassphrase(string path, Dictionary<string, string> flags)
{
    var oldPassphrase = flags.GetValueOrDefault("key-password")
                        ?? Environment.GetEnvironmentVariable("PBS_ENCRYPTION_PASSWORD")
                        ?? ReadSecret("Old key password: ");
    var newPassphrase = flags.GetValueOrDefault("new-password") ?? ReadSecret("New key password: ");
    var fingerprint = KeyFile.ChangePassphrase(path, oldPassphrase, newPassphrase);
    Console.WriteLine($"updated key {path} (fingerprint {fingerprint})");
    return 0;
}

static int KeyCreateMasterKey(List<string> positionals, Dictionary<string, string> flags)
{
    var publicPath = positionals.ElementAtOrDefault(0) ?? "master-public.pem";
    var privatePath = positionals.ElementAtOrDefault(1) ?? "master-private.pem";
    var passphrase = flags.GetValueOrDefault("key-password");
    MasterKey.CreateMasterKey(publicPath, privatePath, passphrase);
    Console.WriteLine($"created master public key {publicPath}");
    Console.WriteLine($"created master private key {privatePath}");
    return 0;
}

static int KeyImportMasterPubkey(string path)
{
    var (fingerprint, keySize) = MasterKey.ValidatePublicKey(path);
    Console.WriteLine($"validated master public key {path} ({keySize} bits, fingerprint {fingerprint})");
    return 0;
}

static int KeyShowMasterPubkey(List<string> positionals)
{
    Console.Write(MasterKey.ShowMasterPubKey(positionals.ElementAtOrDefault(0) ?? "master-public.pem"));
    return 0;
}

static int KeyPaperkey(List<string> positionals, Dictionary<string, string> flags)
{
    var format = flags.GetValueOrDefault("output-format", "text");
    if (format != "text") throw new ArgumentException("--output-format must be 'text'");
    var path = positionals.ElementAtOrDefault(0) ?? flags.GetValueOrDefault("keyfile") ?? Environment.GetEnvironmentVariable("PBS_KEYFILE")
        ?? throw new ArgumentException("paperkey requires <path> or --keyfile/PBS_KEYFILE");

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    Console.WriteLine("-----BEGIN PROXMOX BACKUP KEY-----");
    // Upstream can render a QR code for paperkey recovery; this client intentionally emits text only.
    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("-----END PROXMOX BACKUP KEY-----");
    return 0;
}

static async Task<int> LoginAsync(List<string> positionals, Dictionary<string, string> flags)
{
    var username = flags.GetValueOrDefault("username")
                   ?? positionals.FirstOrDefault()
                   ?? UsernameFromRepository(flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY"))
                   ?? throw new ArgumentException("login requires <user@realm> or --username");
    var server = ResolveServer(flags);
    var password = flags.GetValueOrDefault("password")
                   ?? Environment.GetEnvironmentVariable("PBS_PASSWORD")
                   ?? ReadSecret($"Password for {username}: ");
    var fp = flags.GetValueOrDefault("fingerprint") ?? Environment.GetEnvironmentVariable("PBS_FINGERPRINT");
    var baseUrl = BaseUrlFromServer(server);

    var ticket = await PbsAuth.LoginAsync(
        baseUrl,
        string.IsNullOrEmpty(fp) ? null : CertificateFingerprint.Parse(fp),
        username,
        password,
        prompt => ReadLineRequired(prompt + " "));

    new TicketStore().Save(HostKey(baseUrl), ticket);
    Console.WriteLine($"logged in as {ticket.Username} @ {HostKey(baseUrl)} (ticket cached, valid ~2h)");
    return 0;
}

static int Logout(List<string> positionals, Dictionary<string, string> flags)
{
    var username = flags.GetValueOrDefault("username")
                   ?? positionals.FirstOrDefault()
                   ?? UsernameFromRepository(flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY"))
                   ?? throw new ArgumentException("logout requires <user@realm> or --username");
    var baseUrl = BaseUrlFromServer(ResolveServer(flags));
    new TicketStore().Delete(HostKey(baseUrl), username);
    Console.WriteLine($"logged out {username} @ {HostKey(baseUrl)} (cached ticket deleted)");
    return 0;
}

static async Task<int> StatusAsync(Dictionary<string, string> flags)
{
    using var client = PbsClient.Create(ResolveConnection(flags));
    var s = client.Options.Datastore;
    var status = await client.GetStatusAsync();
    long total = status.TryGetProperty("total", out var t) ? t.GetInt64() : 0;
    long used = status.TryGetProperty("used", out var u) ? u.GetInt64() : 0;
    long avail = status.TryGetProperty("avail", out var a) ? a.GetInt64() : 0;
    double pct = total > 0 ? 100.0 * used / total : 0;
    Console.WriteLine($"datastore {s}: {Gib(used)} used / {Gib(total)} ({pct:0.0}%), {Gib(avail)} available");
    return 0;

    static string Gib(long b) => $"{b / 1024.0 / 1024 / 1024:0.00} GiB";
}

// ---- helpers ----

static PbsConnectionOptions ResolveConnection(Dictionary<string, string> flags)
{
    var repoSpec = flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY")
        ?? throw new ArgumentException("no repository (use --repository or PBS_REPOSITORY)");
    var repo = PbsRepository.Parse(repoSpec);
    if (repo.AuthId is null)
        throw new ArgumentException("repository must include an auth id (user@realm!token@host:datastore or user@realm@host:datastore)");

    var fp = flags.GetValueOrDefault("fingerprint") ?? Environment.GetEnvironmentVariable("PBS_FINGERPRINT");
    var ns = flags.GetValueOrDefault("ns") ?? Environment.GetEnvironmentVariable("PBS_NAMESPACE");
    var auth = flags.GetValueOrDefault("auth");
    if (!string.IsNullOrEmpty(auth) &&
        !string.Equals(auth, "token", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(auth, "ticket", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("--auth must be 'token' or 'ticket'");

    var useTicket = string.Equals(auth, "ticket", StringComparison.OrdinalIgnoreCase) || !repo.AuthId.Contains('!');
    if (useTicket)
    {
        var username = flags.GetValueOrDefault("username") ?? UserFromAuthId(repo.AuthId);
        var host = HostKey(repo.BaseUrl);
        var ticket = new TicketStore().TryLoad(host, username);
        if (ticket is null || !ticket.IsLikelyValid)
            throw new ArgumentException($"no valid cached ticket for {username} @ {host}; run `proxmox-backup-client login {username} --server {host}`");

        return new PbsConnectionOptions
        {
            BaseUrl = repo.BaseUrl,
            Ticket = ticket,
            Fingerprint = string.IsNullOrEmpty(fp) ? null : CertificateFingerprint.Parse(fp),
            Datastore = repo.Datastore,
            Namespace = ns,
        };
    }

    var secret = flags.GetValueOrDefault("password") ?? Environment.GetEnvironmentVariable("PBS_PASSWORD")
        ?? throw new ArgumentException("no token secret (use --password or PBS_PASSWORD)");

    return new PbsConnectionOptions
    {
        BaseUrl = repo.BaseUrl,
        Token = new PbsApiToken(repo.AuthId, secret),
        Fingerprint = string.IsNullOrEmpty(fp) ? null : CertificateFingerprint.Parse(fp),
        Datastore = repo.Datastore,
        Namespace = ns,
    };
}

static string? FindOnPath(string fileName)
{
    var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    return paths.Select(path => Path.Combine(path, fileName)).FirstOrDefault(File.Exists);
}

static async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException($"failed to start {fileName}");
    await process.WaitForExitAsync(ct);
    return process.ExitCode;
}

static string QuoteArg(string value) =>
    "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

static string ResolveServer(Dictionary<string, string> flags)
{
    var server = flags.GetValueOrDefault("server");
    if (!string.IsNullOrWhiteSpace(server)) return server;

    var repoSpec = flags.GetValueOrDefault("repository") ?? Environment.GetEnvironmentVariable("PBS_REPOSITORY");
    if (!string.IsNullOrWhiteSpace(repoSpec)) return ServerFromRepository(repoSpec);

    throw new ArgumentException("server required (use --server <host[:port]> or --repository <repo>)");
}

static Uri BaseUrlFromServer(string server)
{
    var hasExplicitPort = HasExplicitPort(server);
    var value = server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? server
        : $"https://{server}";
    var uri = new Uri(value);
    var b = new UriBuilder(uri);
    if (!hasExplicitPort) b.Port = PbsRepository.DefaultPort;
    if (!b.Path.EndsWith('/')) b.Path += "/";
    return b.Uri;
}

static bool HasExplicitPort(string server)
{
    var value = server;
    if (Uri.TryCreate(server, UriKind.Absolute, out var uri))
        value = uri.Authority;
    var bracket = value.LastIndexOf(']');
    if (bracket >= 0) return bracket + 1 < value.Length && value[bracket + 1] == ':';
    return value.LastIndexOf(':') > value.LastIndexOf('@');
}

static string ServerFromRepository(string repoSpec)
{
    var repo = PbsRepository.Parse(repoSpec);
    return repo.Port == PbsRepository.DefaultPort ? repo.Host : $"{repo.Host}:{repo.Port}";
}

static string? UsernameFromRepository(string? repoSpec)
{
    if (string.IsNullOrWhiteSpace(repoSpec)) return null;
    var repo = PbsRepository.Parse(repoSpec);
    return repo.AuthId is null ? null : UserFromAuthId(repo.AuthId);
}

static string UserFromAuthId(string authId)
{
    var bang = authId.IndexOf('!');
    return bang < 0 ? authId : authId[..bang];
}

static string HostKey(Uri baseUrl) => baseUrl.IsDefaultPort ? baseUrl.Host : $"{baseUrl.Host}:{baseUrl.Port}";

static string ReadLineRequired(string prompt)
{
    Console.Write(prompt);
    return Console.ReadLine() ?? throw new InvalidOperationException("input required");
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? string.Empty;

    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return new string(chars.ToArray());
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
}

static ISnapshotSource CreateSnapshotSource(bool useVss, IReadOnlyCollection<string> volumes)
{
    if (!useVss) return new IdentitySnapshotSource();
#if WINDOWS
    return new Pbs.Vss.VssSnapshotSource(volumes);
#else
    throw new PlatformNotSupportedException("--vss requires the Windows build of proxmox-backup-client");
#endif
}

static LoadedCrypt? LoadCrypt(Dictionary<string, string> flags)
{
    var keyfile = flags.GetValueOrDefault("keyfile") ?? Environment.GetEnvironmentVariable("PBS_KEYFILE");
    if (string.IsNullOrEmpty(keyfile)) return null;
    var passphrase = flags.GetValueOrDefault("key-password") ?? Environment.GetEnvironmentVariable("PBS_ENCRYPTION_PASSWORD");
    var rawKey = KeyFile.LoadRawKey(keyfile, passphrase);
    return new LoadedCrypt(new CryptConfig(rawKey), rawKey, keyfile, KeyFile.ReadMetadata(keyfile));
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

static string? GetString(JsonElement obj, string name) =>
    obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static long? GetInt64(JsonElement obj, string name) =>
    obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

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
    Console.WriteLine("proxmox-backup-client — Proxmox Backup client for Windows");
    Console.WriteLine($"version {version}");
    Console.WriteLine();
    Console.WriteLine("Usage (verbs/flags mirror the upstream proxmox-backup-client where possible):");
    Console.WriteLine("  proxmox-backup-client backup <name.pxar>:<dir> [...] --repository <repo> [--ns <ns>] [--backup-id <id>] [--vss] [--no-incremental]");
    Console.WriteLine("  proxmox-backup-client backup <name.img>:<volume> --image --repository <repo> [--ns <ns>]   (Windows; whole-volume image via VSS)");
    Console.WriteLine("  proxmox-backup-client benchmark [--repository <repo>] [--size <MiB>] [--output-format json]   (throughput diagnostics; network leg only with --repository)");
    Console.WriteLine("  proxmox-backup-client login <user@realm> --server <host[:port]> [--fingerprint <fp>]");
    Console.WriteLine("  proxmox-backup-client logout <user@realm> --server <host[:port]>");
    Console.WriteLine("  proxmox-backup-client restore <type/id/time> <name.pxar> <target-dir> --repository <repo> [--ns <ns>] [--pattern <glob>]");
    Console.WriteLine("  proxmox-backup-client map <type/id/time> <name.img> --repository <repo> [--ns <ns>] [--bind 127.0.0.1] [--port 10809] [--export-name <name>] [--device <instance>]");
    Console.WriteLine("  proxmox-backup-client unmap <instance>");
    Console.WriteLine("  proxmox-backup-client snapshot list --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client snapshot forget <type/id/time> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client snapshot notes <type/id/time> [text] --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client prune <type/id> --keep-last N [--keep-daily N ...] [--dry-run] --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client snapshot protected <type/id/time> true|false --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client change-owner <type/id> <new-owner> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client task list --repository <repo> [--node <node>] [--running] [--limit N] [--output-format json]");
    Console.WriteLine("  proxmox-backup-client task log <upid> --repository <repo> [--node <node>]");
    Console.WriteLine("  proxmox-backup-client task stop <upid> --repository <repo> [--node <node>]");
    Console.WriteLine("  proxmox-backup-client namespace create|list|delete <namespace> [--delete-groups] --repository <repo>");
    Console.WriteLine("  proxmox-backup-client status --repository <repo>");
    Console.WriteLine("  proxmox-backup-client catalog dump <type/id/time> --repository <repo> [--ns <ns>]");
    Console.WriteLine("  proxmox-backup-client key create <path> [--kdf none|scrypt] [--key-password <pw>]");
    Console.WriteLine("  proxmox-backup-client key change-passphrase <path> [--key-password <old>] [--new-password <new>]");
    Console.WriteLine("  proxmox-backup-client key create-master-key [public.pem] [private.pem] [--key-password <pw>]");
    Console.WriteLine("  proxmox-backup-client key import-master-pubkey <path>");
    Console.WriteLine("  proxmox-backup-client key show-master-pubkey [path]");
    Console.WriteLine("  proxmox-backup-client key paperkey [path] [--output-format text]");
    Console.WriteLine();
    Console.WriteLine("backup file mode also accepts repeatable --exclude <glob> (e.g. --exclude *.tmp --exclude /logs/**).");
    Console.WriteLine("snapshot list accepts --output-format json.");
    Console.WriteLine();
    Console.WriteLine("Repository: [AUTHID@]HOST[:PORT]:DATASTORE  (e.g. root@pam!tok@pbs.example:store01 or root@pam@pbs.example:store01 for ticket auth)");
    Console.WriteLine("Credentials: --password/PBS_PASSWORD (token secret or login password), --auth token|ticket, --fingerprint/PBS_FINGERPRINT.");
    Console.WriteLine("Encryption: --keyfile/PBS_KEYFILE [--master-pubkey-file <pem>]; AES-256-GCM,");
    Console.WriteLine("            interoperable with the official client's --keyfile and key commands.");
    Console.WriteLine();
    Console.WriteLine("This is an independent, community project. It is NOT produced, endorsed by, or");
    Console.WriteLine("affiliated with Proxmox Server Solutions GmbH. \"Proxmox\" is their trademark.");
}

internal sealed record LoadedCrypt(CryptConfig Config, byte[] RawKey, string KeyfilePath, KeyFileMetadata? Metadata);
