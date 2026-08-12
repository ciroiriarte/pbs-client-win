# ProjFS M1 Final Verification Report

Date: 2026-08-12T10:16:58Z

Scope: VM144 only, existing snapshot `host/projfs-test/1786506894` in namespace `windows-test`.
No backup, prune, delete, or git push was performed.

## Result

M1 is **not fully verified** in this run.

Verified:

- Browse works.
- Content matches expected sizes and SHA-256 hashes.
- Existing mounted content is read-only.
- Creating a new file in the mount root succeeds, which is the expected documented limitation after reverting the #27 deny-create ACL.

Not verified:

- #28 clean unmount. `unmount` signaled the mount process and PID `4916` exited within 15 seconds, but `Remove-Item C:\mnt\projfs -Recurse -Force` failed with `The provider that supports file system virtualization is temporarily unavailable`. The mount root remained present.

## Source Refresh

Current Linux working tree was repushed to VM144 by tarball, excluding `.git`, `.omc`, `.omx`, `bin`, and `obj`.

Guest extraction result:

```text
REFRESH_OK
```

## Rebuild

Command:

```text
C:\dotnet\dotnet.exe build C:\pbs\src\Pbs.Cli\Pbs.Cli.csproj -f net8.0-windows -c Debug
```

Result: PASS.

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:09.37
```

Output assembly:

```text
C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll
```

## Mount

Detached mount process:

```text
PID: 4916
CommandLine: "C:\dotnet\dotnet.exe" C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll mount host/projfs-test/1786506894 projfs-test C:\mnt\projfs --repository root@pam!windows-test@10.2.0.211:backup01 --ns windows-test
```

Mount log tail:

```text
mounted projfs-test.pxar.didx at C:\mnt\projfs read-only (101071 bytes hydrated)
Press Ctrl-C or run 'proxmox-backup-client unmount C:\mnt\projfs' to unmount.
```

Mount stderr tail: empty.

## Browse

Result: PASS. Expected full tree was present with expected sizes.

```text
FILE a.txt 27
FILE b.bin 100000
FILE empty.dat 0
DIR  sub
FILE sub\c.txt 17
DIR  sub\deep
FILE sub\deep\d.txt 32
```

## Content Hashes

Result: PASS.

| Path | Expected size | Actual size | Expected SHA-256 | Actual SHA-256 | Match |
| --- | ---: | ---: | --- | --- | --- |
| `a.txt` | 27 | 27 | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | `8aa88971759b80b9eaac61d4e66153debadc11609e6c9fc1cb9562dc76cb6501` | yes |
| `b.bin` | 100000 | 100000 | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | `9fa23337b39dbf9829b4b9a9d947bcac2e7cc4c5e94941330c733dbd4738718e` | yes |
| `empty.dat` | 0 | 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | yes |
| `sub\c.txt` | 17 | 17 | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` | `e818f9464639721abf5434bab5d958f81435dee1cb7347efad6ceef78e117bab` | yes |
| `sub\deep\d.txt` | 32 | 32 | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` | `da48e03b87a83ee1164dde3c62801952bcdfa30efac56bc02be98a67b5110507` | yes |

## Existing Content Read-Only

Result: PASS.

Probe:

```text
Set-Content C:\mnt\projfs\a.txt
```

Actual result:

```text
overwrite=FAILED_EXPECTED|Access to the path 'C:\mnt\projfs\a.txt' is denied.
```

## Root Create Limitation

Result: Informational, expected limitation.

Probe:

```text
New-Item C:\mnt\projfs\scratch.txt
```

Actual result:

```text
create=SUCCEEDED_EXPECTED_LIMITATION|C:\mnt\projfs\scratch.txt|length=0
```

## Clean Unmount

Result: FAIL.

Unmount command:

```text
C:\dotnet\dotnet.exe C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll unmount C:\mnt\projfs
```

Actual result:

```text
unmountExitCode=0
unmountOutput=unmount signaled for C:\mnt\projfs
mountPid=4916
mountExitedWithin15s=True
waitSeconds=0.10
removeMountDir=FAILED|The provider that supports file system virtualization is temporarily unavailable
mountDirExistsAfterRemove=True
tempPxarLeftovers=NONE
```

A delayed cleanup attempt after 5 seconds also failed:

```text
delayedRemoveMountDir=FAILED|The provider that supports file system virtualization is temporarily unavailable
mountDirExistsAfterDelayedRemove=True
```

After rebooting VM144 to try to leave the VM tidy, the directory still existed and removal still failed:

```text
postRebootRemove=FAILED|The provider that supports file system virtualization is temporarily unavailable
mountDirExists=True
tempPxarLeftovers=NONE
```

The stale mount root is a ProjFS reparse point:

```text
Reparse Tag Value : 0x9000001c
Tag value: Microsoft
Tag value: Directory
```

Attempting to delete the reparse point directly also failed:

```text
fsutil reparsepoint delete C:\mnt\projfs
Error:  The provider that supports file system virtualization is temporarily unavailable.
```

## Final State

- VM144 was rebooted during cleanup because the stale ProjFS root could not be removed after the mount process exited.
- `C:\mnt\projfs` still exists and is not tidy; Windows reports the ProjFS provider-unavailable error on removal.
- No `pbs-projfs-*.pxar` temp files were found in `%TEMP%` or `C:\Windows\Temp`.
- No mount process remained after unmount.

