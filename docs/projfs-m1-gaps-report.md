# ProjFS M1 gaps VM144 report

Date: 2026-08-12

Scope:
- VM used: VM144 only.
- PBS operation: read-only access to existing snapshot `host/projfs-test/1786506894` only.
- Namespace: `windows-test`.
- Source delivery: current Linux working tree tarball, excluding `.git`, `.omc`, `.omx`, `bin`, and `obj`, served from the PVE host and extracted to `C:\pbs`.
- Git push: not performed.
- On-VM code fixes: none.

## Summary

| Check | Result | Evidence |
| --- | --- | --- |
| Rebuild | PASS | `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`. |
| Browse still works after #27 ACL | FAIL | `Get-ChildItem -Recurse C:\mnt\projfs` failed with access denied; direct reads also failed. |
| #27 deny-create | PARTIAL / NOT ACCEPTABLE | New file and new directory creation were denied, but the same ACL broke normal browse/read. |
| #28 clean unmount | PASS | `unmount` signaled PID 3504, process exited within 15s, mount dir removed without provider error, temp pxar gone. |

Critical verdict: #27 ACL must be reverted or redesigned. It blocks creation, but it also breaks normal ProjFS browse/read.

#28 verdict: works in this run.

## Snapshot Sanity Check

Read-only catalog dump of the requested snapshot confirmed the expected test tree and sizes:

```text
/projfs-test.pxar.didx	-
/projfs-test.pxar.didx/a.txt	27
/projfs-test.pxar.didx/b.bin	100000
/projfs-test.pxar.didx/empty.dat	0
/projfs-test.pxar.didx/sub	-
/projfs-test.pxar.didx/sub/c.txt	17
/projfs-test.pxar.didx/sub/deep	-
/projfs-test.pxar.didx/sub/deep/d.txt	32
```

## Rebuild

Command:

```powershell
C:\dotnet\dotnet.exe build C:\pbs\src\Pbs.Cli\Pbs.Cli.csproj -f net8.0-windows -c Debug
```

Result: PASS

```text
Pbs.Cli -> C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

No `System.Security.AccessControl.*`, `GetAccessControl`, or `SetAccessControl` compiler errors occurred.

## Mount Startup

Detached mount started via `Start-Process -PassThru`; mount process PID: 3504.

Mount log:

```text
mounted projfs-test.pxar.didx at C:\mnt\projfs read-only (101071 bytes hydrated)
Press Ctrl-C or run 'proxmox-backup-client unmount C:\mnt\projfs' to unmount.
```

Mount stderr tail: empty.

## Browse Still Works

Result: FAIL.

This is the critical #27 regression. The deny-create ACL broke normal ProjFS browse/read:

```text
Get-ChildItem -Recurse C:\mnt\projfs:
Access to the path 'C:\mnt\projfs\sub' is denied.

Read C:\mnt\projfs\a.txt:
Access to the path 'C:\mnt\projfs\a.txt' is denied.

Read C:\mnt\projfs\sub\deep\d.txt:
Access to the path 'C:\mnt\projfs\sub\deep\d.txt' is denied.
```

Verdict for #27: does not work as implemented. The ACL must be reverted or changed because browse/read failed before content verification could compare sizes/first bytes.

## #27 Deny Create

Result: create operations were denied, but this is not acceptable because browse was also denied.

```text
New-Item C:\mnt\projfs\newfile.txt:
Access to the path 'C:\mnt\projfs\newfile.txt' is denied.

New-Item C:\mnt\projfs\newdir:
Access to the path 'newdir' is denied.
```

Verdict for #27: fails acceptance overall.

## #28 Clean Unmount

Command:

```powershell
C:\dotnet\dotnet.exe C:\pbs\src\Pbs.Cli\bin\Debug\net8.0-windows\proxmox-backup-client.dll unmount C:\mnt\projfs
```

Result: PASS.

Evidence:

```text
unmount signaled for C:\mnt\projfs
process PID 3504 exited within 15 seconds: yes
Remove-Item C:\mnt\projfs -Recurse -Force: succeeded
provider temporarily unavailable error: not seen
remaining pbs-projfs-*.pxar temp files: 0
```

Verdict for #28: works in this run.

## Cleanup

- Mount process after cleanup: none.
- `C:\mnt\projfs` after cleanup: absent.
- Temp `pbs-projfs-*.pxar` files after cleanup: none.
- Temporary PVE HTTP unit `pbs-http.service`: stopped; final state `inactive`.
- On-VM fix diff: none; no on-VM code changes were made.
