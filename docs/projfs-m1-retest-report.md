# ProjFS Mount M1 Re-Test Report

Date: 2026-08-12

Target: VM144 (`10.2.0.148`) via PVE `root@bigiron.lan`, guest exec as SYSTEM.

Snapshot: `host/projfs-test/1786506894`, namespace `windows-test`.

Result: M1 acceptance for browse full tree, content hashes, and existing-content read-only is MET. Teardown is not clean: graceful `taskkill` without `/f` failed, force-kill stopped the process, but the mount directory remained attached with the ProjFS provider-unavailable error.

## Source Transfer

Current Linux working tree was packaged and copied to VM144, replacing `C:\pbs`:

```text
tar czf /tmp/pbs-src.tgz -C /home/ciro/code/pbs-client-win --exclude=.git --exclude=.omc --exclude=bin --exclude=obj .
```

Transfer path used: local `/tmp/pbs-src.tgz` -> PVE `/tmp/pbs-src.tgz` -> HTTP from `10.2.0.210:8088` -> guest `C:\pbs-src.tgz` -> extracted to `C:\pbs`.

No git push was performed. No backup, prune, delete, or snapshot mutation was performed.

## Source Diff Captured

No on-VM source fixes were applied during this retest. The Linux working-tree diff that was retested:

```diff
diff --git a/src/Pbs.Cli/Program.cs b/src/Pbs.Cli/Program.cs
index 0a5c290..dbb3e65 100644
--- a/src/Pbs.Cli/Program.cs
+++ b/src/Pbs.Cli/Program.cs
@@ -332,6 +332,13 @@ static async Task<int> MountAsync(List<string> positionals, Dictionary<string, s
         cts.Cancel();
     };
     Console.CancelKeyPress += cancelHandler;
+    // Console.CancelKeyPress only fires with an attached console; a detached mount
+    // process (Start-Process with redirected output) is stopped via signals, so also
+    // catch SIGINT/SIGTERM to run a clean StopVirtualizing + teardown.
+    using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
+        System.Runtime.InteropServices.PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });
+    using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
+        System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
 
     try
     {
@@ -342,6 +349,10 @@ static async Task<int> MountAsync(List<string> positionals, Dictionary<string, s
         using var catalogBuffer = new MemoryStream();
         await reader.RestoreDynamicArchiveAsync("catalog.pcat1.didx", catalogBuffer, crypt, cts.Token);
         var catalog = PxarCatalog.Parse(catalogBuffer.ToArray());
+        // The catalog root lists archives as children (e.g. "<name>.pxar.didx"); the mount
+        // root should expose the selected archive's contents, not the archive list.
+        var archiveCatalog = catalog.Children.FirstOrDefault(c => string.Equals(c.Name, stored, StringComparison.OrdinalIgnoreCase))
+            ?? throw new InvalidDataException($"catalog does not contain archive root '{stored}'");
 
         await using var tempStream = new FileStream(
             tempPath,
@@ -355,7 +366,7 @@ static async Task<int> MountAsync(List<string> positionals, Dictionary<string, s
         tempStream.Position = 0;
         var index = await PxarIndexer.BuildIndexAsync(tempStream, cts.Token);
 
-        using var session = new Pbs.ProjFs.PxarMountSession(root, catalog, tempStream, index);
+        using var session = new Pbs.ProjFs.PxarMountSession(root, archiveCatalog, tempStream, index);
         session.Start();
 
         Console.WriteLine($"mounted {stored} at {root} read-only ({restored} bytes hydrated)");
diff --git a/src/Pbs.ProjFs/PxarMountSession.cs b/src/Pbs.ProjFs/PxarMountSession.cs
index dd52014..ffb0ea5 100644
--- a/src/Pbs.ProjFs/PxarMountSession.cs
+++ b/src/Pbs.ProjFs/PxarMountSession.cs
@@ -134,8 +134,11 @@ public sealed class PxarMountSession : IDisposable
             return HResult.FileNotFound;
 
         var time = ToDateTime(entry.MtimeSecs);
+        // ProjFS requires the native (backslash) relative path here; NormalizePath's
+        // forward slashes are only for our internal lookup keys. Passing a '/'-containing
+        // path makes WritePlaceholderInfo produce ERROR_INVALID_NAME for nested entries.
         return _instance.WritePlaceholderInfo(
-            NormalizePath(relativePath),
+            relativePath,
             time,
             time,
             time,
```

## Snapshot Confirmation

The snapshot list from VM144 included the requested snapshot:

```text
host/projfs-test/1786506657
host/projfs-test/1786506894
```

## Rebuild Result

Command:

```text
C:\dotnet\dotnet.exe build C:\pbs\src\Pbs.Cli\Pbs.Cli.csproj -f net8.0-windows -c Debug
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.65
```

Output assembly:

```text
C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll
```

## Mount

Detached mount started with redirected logs:

```text
C:\projfs-mount.log
C:\projfs-mount.err
```

Mount process PID: `2412`.

Mount log:

```text
mounted projfs-test.pxar.didx at C:\mnt\projfs read-only (101071 bytes hydrated)
Press Ctrl-C to unmount.
```

Mount stderr was empty.

## Recursive Enumeration

Expected:

```text
FILE  a.txt             27
FILE  b.bin             100000
FILE  empty.dat         0
DIR   sub
FILE  sub\c.txt         17
DIR   sub\deep
FILE  sub\deep\d.txt    32
```

Actual:

```text
FILE    a.txt           27
FILE    b.bin           100000
FILE    empty.dat       0
DIR     sub
FILE    sub\c.txt       17
DIR     sub\deep
FILE    sub\deep\d.txt  32
```

Result: PASS. The previously failing nested directory enumeration did not produce `ERROR_INVALID_NAME`.

## SHA-256 Verification

| File | Size | Actual SHA-256 | Expected SHA-256 | Match |
| --- | ---: | --- | --- | --- |
| `a.txt` | 27 | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | PASS |
| `b.bin` | 100000 | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | PASS |
| `empty.dat` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | PASS |
| `sub\c.txt` | 17 | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` | PASS |
| `sub\deep\d.txt` | 32 | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` | PASS |

Result: PASS for all five files, including nested `sub\c.txt` and `sub\deep\d.txt`.

## Read-Only Checks

Existing content overwrite:

```text
OVERWRITE_RESULT    FAILED    System.UnauthorizedAccessException    Access to the path 'C:\mnt\projfs\a.txt' is denied.
```

Result: PASS. Existing mounted content is read-only.

Create new root file:

```text
CREATE_RESULT       SUCCEEDED 16
```

Result: informational only. This is the known M1 gap and was not counted as a failure for this run.

## Teardown

Graceful stop attempt:

```text
taskkill /pid 2412
ERROR: The process with PID 2412 could not be terminated.
Reason: This process can only be terminated forcefully (with /F option).
```

Fallback force-kill:

```text
taskkill /pid 2412 /f
SUCCESS: The process with PID 2412 has been terminated.
PROCESS_STATUS    EXITED
```

Mount directory removal after force-kill:

```text
RMDIR_RESULT    FAILED    System.ComponentModel.Win32Exception    The provider that supports file system virtualization is temporarily unavailable
```

Delayed retry:

```text
RMDIR_RETRY     FAILED    System.ComponentModel.Win32Exception    The provider that supports file system virtualization is temporarily unavailable
PATH_STATUS     EXISTS
```

Temp pxar files:

```text
TEMP_RESULT     NONE
TEMP_RETRY      NONE
```

Teardown result: FAIL for clean teardown. The detached mount did not respond to `taskkill` without `/f`; after force-kill, the ProjFS filter remained attached to `C:\mnt\projfs`, so the directory could not be removed. Temp `pbs-projfs-*.pxar` files were gone.

## Acceptance

M1 acceptance criteria from this retest:

| Criterion | Result |
| --- | --- |
| Browse full recursive tree | MET |
| All five file contents match expected SHA-256 | MET |
| Existing mounted content is read-only | MET |

M1 acceptance is MET for browse, content, and existing-content read-only behavior.

Separate teardown criterion is NOT MET: clean detached shutdown did not run `StopVirtualizing` under `taskkill /pid` without `/f`, and force-kill left the provider attached.
