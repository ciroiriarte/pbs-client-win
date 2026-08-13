# WinFsp Server Core Functional Test Report — VM143

**STATUS: PENDING LIVE RUN.** The dual-engine `mount --provider winfsp` code is
landed (see `docs/WINFSP.md`, `IPbsMountProvider`), but this checklist has not
yet been executed against VM143. Sections below are a template: run the steps,
paste real output, and fill in every RESULT / PASS-FAIL field before this
report is considered complete.

Date: _(pending)_

Target: VM143 (Server Core, PVE guest agent via `qm guest exec 143`).

Live PBS target: `root@pam!windows-test@pbs-01.lan:backup01`, namespace
`windows-test` (adjust `<repo>` below if different).

Result: **PENDING**

## Environment

| Item | Value |
| --- | --- |
| Guest | VM143 |
| OS | Windows Server 2022, **Core** installation (no Desktop Experience) |
| ProjFS availability | **Confirmed unavailable** — `Get-WindowsOptionalFeature` lists 245 features, 0 ProjFS; `Enable-WindowsOptionalFeature -FeatureName Client-ProjFS` returns `Feature name Client-ProjFS is unknown`. |
| Mount engine under test | WinFsp (`--provider winfsp`) |

## Preconditions

- WinFsp installed on VM143 from **https://winfsp.dev/**, as Administrator
  (see `docs/WINFSP.md`).
- A pbs-client-win build present on VM143 (`proxmox-backup-client.exe` /
  `.dll`) with the `mount --provider` dual-engine code.
- A PBS snapshot containing a pxar archive to browse (reuse an existing one,
  e.g. `host/projfs-test/<timestamp>`, or create a fresh one with `backup`).
- Network reachability to `pbs-01.lan` from VM143.

## Checklist

All commands are PowerShell, run in an elevated (Administrator) session on
VM143 unless noted. Replace `<repo>`, `<snapshot>`, and `<archive>` with real
values before running.

### 1. Install / verify WinFsp

```powershell
# If not already installed, download and run the WinFsp installer from
# https://winfsp.dev/ as Administrator, then verify:
Test-Path "$env:ProgramFiles(x86)\WinFsp\bin\winfsp-x64.dll"
Test-Path "$env:SystemRoot\System32\drivers\winfsp-x64.sys"
Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\WinFsp" -Name InstallDir
```

RESULT: _(pending)_
PASS/FAIL: _(pending)_

### 2. Mount to a drive letter

```powershell
$repo = "<repo>"
$snapshot = "host/projfs-test/<timestamp>"
$archive = "root.pxar"

Start-Process proxmox-backup-client.exe -ArgumentList `
    'mount', $snapshot, $archive, 'X:', '--provider', 'winfsp', '--repository', $repo `
    -RedirectStandardOutput C:\winfsp-mount-drive.log `
    -RedirectStandardError C:\winfsp-mount-drive.err

Start-Sleep -Seconds 3
Get-Content C:\winfsp-mount-drive.log
Get-Content C:\winfsp-mount-drive.err
Get-PSDrive X
```

RESULT: _(pending)_
PASS/FAIL: _(pending)_

### 3. Mount to a directory

```powershell
New-Item -ItemType Directory -Path C:\mnt\winfsp -Force | Out-Null

Start-Process proxmox-backup-client.exe -ArgumentList `
    'mount', $snapshot, $archive, 'C:\mnt\winfsp', '--provider', 'winfsp', '--repository', $repo `
    -RedirectStandardOutput C:\winfsp-mount-dir.log `
    -RedirectStandardError C:\winfsp-mount-dir.err

Start-Sleep -Seconds 3
Get-Content C:\winfsp-mount-dir.log
Get-Content C:\winfsp-mount-dir.err
```

RESULT: _(pending)_
PASS/FAIL: _(pending)_

### 4. Browse the tree (drive-letter mount)

```powershell
Get-ChildItem X:\ -Recurse | Select-Object FullName, Length, PSIsContainer
```

Expected: full recursive tree of the archive (files and subdirectories),
matching the archive's known contents.

RESULT: _(pending)_
PASS/FAIL: _(pending)_

### 5. Content verification — SHA-256 against `restore`

```powershell
# Pick a file present in the archive, e.g. sub\c.txt
$relPath = "sub\c.txt"

# Hash via the mount
$mountHash = (Get-FileHash "X:\$relPath" -Algorithm SHA256).Hash

# Hash via restore of the same file to a scratch location
New-Item -ItemType Directory -Path C:\winfsp-restore-check -Force | Out-Null
proxmox-backup-client.exe restore $snapshot $archive C:\winfsp-restore-check `
    --repository $repo
$restoreHash = (Get-FileHash "C:\winfsp-restore-check\$relPath" -Algorithm SHA256).Hash

Write-Output "mount:   $mountHash"
Write-Output "restore: $restoreHash"
Write-Output "match:   $($mountHash -eq $restoreHash)"
```

RESULT: _(pending — mount hash / restore hash / match)_
PASS/FAIL: _(pending)_

### 6. Read-only enforcement

```powershell
# Overwrite an existing file — expect failure
try {
    "tampered" | Out-File -FilePath "X:\$relPath" -Encoding ascii -ErrorAction Stop
    Write-Output "OVERWRITE: succeeded (FAIL — should have been denied)"
} catch {
    Write-Output "OVERWRITE: denied as expected ($($_.Exception.Message))"
}

# Delete an existing file — expect failure
try {
    Remove-Item "X:\$relPath" -ErrorAction Stop
    Write-Output "DELETE: succeeded (FAIL — should have been denied)"
} catch {
    Write-Output "DELETE: denied as expected ($($_.Exception.Message))"
}

# Create a new file — expect failure
try {
    "new" | Out-File -FilePath "X:\newfile.txt" -Encoding ascii -ErrorAction Stop
    Write-Output "CREATE: succeeded (FAIL — should have been denied)"
} catch {
    Write-Output "CREATE: denied as expected ($($_.Exception.Message))"
}
```

RESULT: _(pending — overwrite / delete / create outcomes)_
PASS/FAIL: _(pending)_

### 7. Clean unmount — drive-letter mount

```powershell
proxmox-backup-client.exe unmount X:

Start-Sleep -Seconds 2
Get-PSDrive X -ErrorAction SilentlyContinue
Get-Process proxmox-backup-client -ErrorAction SilentlyContinue
```

Expected: `X:` no longer resolves as a drive; no lingering
`proxmox-backup-client` mount process.

RESULT: _(pending)_
PASS/FAIL: _(pending)_

### 8. Clean unmount — directory mount

```powershell
proxmox-backup-client.exe unmount C:\mnt\winfsp

Start-Sleep -Seconds 2
Get-ChildItem C:\mnt\winfsp -ErrorAction SilentlyContinue
Remove-Item C:\mnt\winfsp -Force -ErrorAction SilentlyContinue
```

Expected: mount point empty/removable, no residue, no lingering mount
process.

RESULT: _(pending)_
PASS/FAIL: _(pending)_

## Summary

| Check | Result |
| --- | --- |
| WinFsp install/verify | _(pending)_ |
| Mount — drive letter | _(pending)_ |
| Mount — directory | _(pending)_ |
| Recursive browse | _(pending)_ |
| SHA-256 mount vs. restore | _(pending)_ |
| Read-only: overwrite denied | _(pending)_ |
| Read-only: delete denied | _(pending)_ |
| Read-only: create denied | _(pending)_ |
| Unmount — drive letter, clean | _(pending)_ |
| Unmount — directory, clean | _(pending)_ |

**Verdict: PENDING.** Fill in every RESULT/PASS-FAIL field above after
executing this checklist on VM143.
