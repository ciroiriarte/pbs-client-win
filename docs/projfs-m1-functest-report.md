# ProjFS M1 Functional Test Report

Date: 2026-08-12

Target: VM144 only (`10.2.0.148`, PVE guest agent via `qm guest exec 144`).

Live PBS target: `root@pam!windows-test@pbs-01.lan:backup01`, namespace `windows-test`.

Result: **FAILED after one small VM144-only fix**. Root files hydrated correctly after the fix, but recursive enumeration failed for nested directories and the mount allowed creating a new file, so M1 is not functionally complete.

No Linux repo code was modified. The fix below exists only in `C:\pbs` on VM144 and is included so it can be applied deliberately to the Linux repo later.

## VM144 Build Used

Before testing, key staged source files on VM144 matched the Linux repo SHA-256:

| File | SHA-256 |
| --- | --- |
| `src/Pbs.Cli/Program.cs` | `8a6b2b1eb7488b466c4a8b2e5318223ece9f7ee82cef85ec4b216ca7287df205` |
| `src/Pbs.ProjFs/PxarMountSession.cs` | `f05e7ee8c7e4f7a4d518985a663e1f9e0751912abfbd3215a67c8a8a314142a0` |
| `src/Pbs.ProjFs/Pbs.ProjFs.csproj` | `04f918220bf6a78680e017bd352021c881b6c16a6e62d955a61814848a2275bf` |

After the VM-only fix, `C:\dotnet\dotnet.exe build C:\pbs\src\Pbs.Cli\Pbs.Cli.csproj -f net8.0-windows -c Debug` passed with 0 warnings and 0 errors.

## Source Tree

Created in guest at `C:\projfs-src`.

| Path | Size | Source SHA-256 |
| --- | ---: | --- |
| `a.txt` | 27 | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` |
| `b.bin` | 100000 | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` |
| `empty.dat` | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| `sub/c.txt` | 17 | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` |
| `sub/deep/d.txt` | 32 | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` |

`b.bin` used a deterministic 100000-byte pattern: byte `i = ((i * 37 + 11) % 256)`.

## Backup / Snapshot

Command shape:

```text
C:\dotnet\dotnet.exe C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll backup projfs-test.pxar:C:\projfs-src --repository <repo> --backup-id projfs-test
```

Snapshot used for final fixed-VM run:

```text
host/projfs-test/1786506894
```

Backup stdout tail:

```text
projfs-test.pxar: C:\projfs-src -> projfs-test.pxar.didx (101071 bytes)
  chunks: 1 total, 1 uploaded, 0 reused
catalog.pcat1.didx: 132 bytes (1 uploaded)
backup complete: host/projfs-test/1786506894
```

Snapshot list confirmed `host/projfs-test/1786506894`.

Catalog dump confirmed the expected backup contents under the archive catalog root:

```text
/projfs-test.pxar.didx    -
/projfs-test.pxar.didx/a.txt    27
/projfs-test.pxar.didx/b.bin    100000
/projfs-test.pxar.didx/empty.dat    0
/projfs-test.pxar.didx/sub    -
/projfs-test.pxar.didx/sub/c.txt    17
/projfs-test.pxar.didx/sub/deep    -
/projfs-test.pxar.didx/sub/deep/d.txt    32
```

## Mount

Command shape:

```text
Start-Process C:\dotnet\dotnet.exe -ArgumentList 'C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll','mount','host/projfs-test/1786506894','projfs-test','C:\mnt\projfs','--repository','<repo>' -RedirectStandardOutput C:\projfs-mount.log -RedirectStandardError C:\projfs-mount.err
```

`C:\projfs-mount.log` tail:

```text
mounted projfs-test.pxar.didx at C:\mnt\projfs read-only (101071 bytes hydrated)
Press Ctrl-C to unmount.
```

`C:\projfs-mount.err` tail: empty.

## Enumeration

Expected recursive tree:

| Path | Kind | Size |
| --- | --- | ---: |
| `a.txt` | file | 27 |
| `b.bin` | file | 100000 |
| `empty.dat` | file | 0 |
| `sub` | dir | |
| `sub/c.txt` | file | 17 |
| `sub/deep` | dir | |
| `sub/deep/d.txt` | file | 32 |

Actual root listing after the VM-only archive-root fix:

| Path | Kind | Size |
| --- | --- | ---: |
| `a.txt` | file | 27 |
| `b.bin` | file | 100000 |
| `empty.dat` | file | 0 |
| `sub` | dir | |

Recursive enumeration failed:

```text
System.IO.IOException: The filename, directory name, or volume label syntax is incorrect.
   at System.IO.__Error.WinIOError(Int32 errorCode, String maybeFullPath)
   at System.IO.FileSystemEnumerableIterator`1.CommonInit()
   at System.IO.FileSystemEnumerableIterator`1..ctor(String path, String originalUserPath, String searchPattern, SearchOption searchOption, SearchResultHandler`1 resultHandler, Boolean checkHost)
   at System.IO.DirectoryInfo.EnumerateDirectories()
   at Microsoft.PowerShell.Commands.FileSystemProvider.Dir(DirectoryInfo directory, Boolean recurse, UInt32 depth, Boolean nameOnly, ReturnContainers returnContainers)
```

Direct path checks showed nested children were not reachable:

| Path | Exists |
| --- | --- |
| `sub/c.txt` | no |
| `sub/deep` | no |
| `sub/deep/d.txt` | no |

## Content Hash Verification

| Path | Source SHA-256 | Mounted SHA-256 | Match |
| --- | --- | --- | --- |
| `a.txt` | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | yes |
| `b.bin` | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | yes |
| `empty.dat` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | yes |
| `sub/c.txt` | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` | not reachable | no |
| `sub/deep/d.txt` | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` | not reachable | no |

Root-level hydration, including the 100000-byte `b.bin`, works. Nested directory traversal does not.

## Read-Only Check

| Action | Expected | Actual |
| --- | --- | --- |
| Overwrite `C:\mnt\projfs\a.txt` | fail | failed: access denied |
| Create `C:\mnt\projfs\new.txt` | fail | **succeeded** |

Read-only behavior is therefore incomplete.

## Teardown

After the failed recursive run, the detached mount process had already exited but ProjFS placeholders remained. `Remove-Item C:\mnt\projfs -Recurse -Force` initially failed with:

```text
The provider that supports file system virtualization is temporarily unavailable
```

Final cleanup succeeded with `rmdir /s /q C:\mnt\projfs` after confirming the ProjFS reparse point state. Final VM state:

```text
C:\mnt\projfs absent
No proxmox-backup-client.dll mount process
No pbs-projfs-*.pxar temp files
```

## VM-Only Fix Applied

Initial run before this fix created snapshot `host/projfs-test/1786506657`; mount logged success but `C:\mnt\projfs\a.txt` never appeared. Diagnosis: the PBS catalog root contains archive entries such as `projfs-test.pxar.didx`, but the mount handed the full catalog root to `PxarMountSession`. The ProjFS mount root should expose the selected archive contents.

Exact diff applied only to `C:\pbs\src\Pbs.Cli\Program.cs` on VM144:

```diff
diff --git a/src/Pbs.Cli/Program.cs b/src/Pbs.Cli/Program.cs
--- a/src/Pbs.Cli/Program.cs
+++ b/src/Pbs.Cli/Program.cs
@@
         using var catalogBuffer = new MemoryStream();
         await reader.RestoreDynamicArchiveAsync("catalog.pcat1.didx", catalogBuffer, crypt, cts.Token);
         var catalog = PxarCatalog.Parse(catalogBuffer.ToArray());
+        var archiveCatalog = catalog.Children.FirstOrDefault(child =>
+            string.Equals(child.Name, stored, StringComparison.OrdinalIgnoreCase))
+            ?? throw new InvalidDataException($"catalog does not contain archive root '{stored}'");
 
         await using var tempStream = new FileStream(
@@
-        using var session = new Pbs.ProjFs.PxarMountSession(root, catalog, tempStream, index);
+        using var session = new Pbs.ProjFs.PxarMountSession(root, archiveCatalog, tempStream, index);
         session.Start();
```

## Diagnosis

Confirmed:

- Backup creation and catalog upload work.
- `mount` can hydrate the pxar and start ProjFS.
- After selecting the archive catalog subtree, root-level file placeholders and `GetFileData` work.

Still failing:

- Recursive directory enumeration into `sub` fails with `System.IO.IOException: The filename, directory name, or volume label syntax is incorrect`.
- Direct nested paths under `sub` are not reachable.
- Creating a new file in the virtual root succeeds, so read-only enforcement does not cover create operations.
- Teardown after provider exit can leave ProjFS placeholders until the root is forcibly removed.

Likely next code areas:

- `PxarMountSession.GetPlaceholderInfo` / directory placeholder metadata for child directories.
- ProjFS notification mapping: current mapping denies delete/rename/hardlink/pre-convert, but not ordinary file create.
- Graceful mount process shutdown path for detached usage.
