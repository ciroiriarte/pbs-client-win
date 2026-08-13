# WinFsp Server Core Functional Test Report — VM143

**STATUS: PASS.** The dual-engine `mount --provider winfsp` code was executed
end-to-end against a real Windows **Server Core** guest (VM143). WinFsp mounts
(drive letter and directory) browse a pxar archive read-only and on-demand;
content SHA-256 matches a full `restore`; writes/deletes are denied; unmount is
clean. See `docs/WINFSP.md` for the dependency and `IPbsMountProvider` for the
engine abstraction.

Date: 2026-08-13.

Driver: run headlessly via the PVE guest agent (`qm guest exec 143 -- powershell …`)
on host `bigiron` — no RDP/Desktop needed (see `docs/TESTING.md`).

Live PBS target: `root@pam!windows-test@pbs-01:backup01`, namespace
`windows-test`. Snapshot: `host/projfs-test/2026-08-12T03:54:54Z`, archive
`projfs-test.pxar` (a small fixture: `a.txt`, `b.bin` 100 000 bytes,
`empty.dat`, `sub\c.txt`, `sub\deep\d.txt`).

Result: **PASS**

## Environment

| Item | Value |
| --- | --- |
| Guest | VM143 (`pbs-win-wnbd-test`), PVE guest agent via `qm guest exec 143` |
| OS | Windows Server 2022 Standard, **Core** installation — confirmed (`explorer.exe` absent) |
| ProjFS availability | **Confirmed unavailable** — `%SystemDirectory%\drivers\prjflt.sys` absent; `mount --provider projfs` on this box prints the ProjFS enable-guidance and refuses (as designed). |
| .NET | SDK 8.0.424 (installed to `C:\dotnet`) |
| WinFsp | **2.1.25156** (latest stable, v2.1), matching the pinned `winfsp.net` binding — `InstallDir = C:\Program Files (x86)\WinFsp\`, `bin\winfsp-x64.dll` present, `WinFsp.Launcher` service Running |
| Build | `dotnet build src\Pbs.Cli -c Release -f net8.0-windows` → succeeded, 0 warnings; `winfsp-msil.dll` present in output |
| Mount engine under test | WinFsp (`--provider winfsp`) |

## Results

### 1. WinFsp install / detection — PASS

`WinFspAvailability` detects the install via the registry `InstallDir`
(`HKLM\SOFTWARE\WOW6432Node\WinFsp`) plus `bin\winfsp-x64.dll`; both present.
`WinFsp.Launcher` service is Running. Installed silently with
`msiexec /i winfsp-2.1.25156.msi /qn /norestart` (exit 0, no reboot needed).

### 2. Mount to a drive letter (`X:`) — PASS

```
mount host/projfs-test/2026-08-12T03:54:54Z projfs-test.pxar X: --provider winfsp --ns windows-test
```
stdout:
```
mounted projfs-test.pxar.didx at X: read-only via winfsp (101071 bytes, on-demand)
Press Ctrl-C or run 'proxmox-backup-client unmount X:' to unmount.
```
`X:` resolved as a drive; the mount process detached and served on demand.

### 3. Mount to a directory (`C:\mnt\winfsp`) — PASS

```
mount host/projfs-test/2026-08-12T03:54:54Z projfs-test.pxar C:\mnt\winfsp --provider winfsp --ns windows-test
```
stdout: `mounted projfs-test.pxar.didx at C:\mnt\winfsp read-only via winfsp (101071 bytes, on-demand)`.
WinFsp **creates the leaf mount directory itself** (and removes it on unmount),
so the target's **parent must exist** (`C:\mnt` here). Mounting under a missing
parent fails with a clear WinFsp NTSTATUS error from `WinFspMountProvider.Start`
— it is not silent. Drive-letter targets have no such requirement.

### 4. Recursive browse (drive-letter mount) — PASS

`Get-ChildItem X:\ -Recurse` returned the full tree, sizes matching the fixture:

| Path | Bytes |
| --- | --- |
| `X:\a.txt` | 27 |
| `X:\b.bin` | 100000 |
| `X:\empty.dat` | 0 |
| `X:\sub\c.txt` | 17 |
| `X:\sub\deep\d.txt` | 32 |

### 5. Content verification — SHA-256 mount vs. `restore` — PASS

Hashes computed by reading **through the mount** vs. a full `restore` of the
same archive to a scratch dir:

| File | Mount SHA-256 | Restore SHA-256 | Match |
| --- | --- | --- | --- |
| `a.txt` (27 B, single read) | `8AA88971…76CB6501` | `8AA88971…76CB6501` | ✅ |
| `b.bin` (100 000 B, multi-chunk ranged reads) | `9FA23337…4738718E` | `9FA23337…4738718E` | ✅ |

`b.bin` exercises repeated WinFsp `Read` callbacks at increasing offsets (the
on-demand ranged-read path); bytes are identical to a full restore. A byte copy
off the mount (`Copy-Item X:\b.bin …`) produced exactly 100 000 bytes.

### 6. Read-only enforcement — PASS

Against the drive-letter mount:

| Attempt | Outcome |
| --- | --- |
| Write a new file (`Set-Content X:\__ro_test.txt`) | **Denied** (blocked) |
| Delete an existing file (`Remove-Item X:\a.txt`) | **Denied** (blocked) |

Every entry carries `FILE_ATTRIBUTE_READONLY`, the security descriptor grants
read+execute only, and mutating callbacks return `STATUS_MEDIA_WRITE_PROTECTED`.

### 7. Clean unmount — drive letter — PASS

`unmount X:` → `unmount signaled for X:`; within the poll window `X:` no longer
resolved and the detached mount process had exited. No residue.

### 8. Clean unmount — directory — PASS

`unmount C:\mnt\winfsp` → mount process exited; WinFsp removed the leaf mount
directory (`C:\mnt\winfsp` no longer exists). No residue.

## Summary

| Check | Result |
| --- | --- |
| WinFsp install/detection | ✅ PASS |
| Mount — drive letter (`X:`) | ✅ PASS |
| Mount — directory (`C:\mnt\winfsp`) | ✅ PASS |
| Recursive browse | ✅ PASS (5 files, nested dirs, correct sizes) |
| SHA-256 mount vs. restore — `a.txt` | ✅ PASS (match) |
| SHA-256 mount vs. restore — `b.bin` (100 KB, multi-chunk) | ✅ PASS (match) |
| Read-only: write denied | ✅ PASS |
| Read-only: delete denied | ✅ PASS |
| Unmount — drive letter, clean | ✅ PASS |
| Unmount — directory, clean | ✅ PASS |
| ProjFS refused on Core (`--provider projfs`) | ✅ PASS (enable-guidance printed) |

**Verdict: PASS.** The WinFsp mount engine works on Windows Server Core, where
ProjFS is unavailable — issue #29's motivating case. Browse + on-demand ranged
content are correct (byte-identical to `restore`), the mount is strictly
read-only, and unmount is clean for both drive-letter and directory targets.

### Note for maintainers

- Directory-target mounts require the **parent** directory to already exist
  (WinFsp creates and later removes the leaf). A future UX nicety could have the
  CLI create the parent chain for a WinFsp directory target; the drive-letter
  path (the primary Server Core use) needs nothing.
- The pinned `winfsp.net` binding (`2.1.25156`) was validated against the
  matching stable native WinFsp v2.1; WinFsp keeps a stable native ABI, so newer
  stable natives are expected to work too (detect-and-require).
