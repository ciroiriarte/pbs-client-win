# pbs-client-win — native Windows Proxmox Backup client

A **Windows-native** backup client for [Proxmox Backup Server](https://pbs.proxmox.com), written
from scratch in **C# / .NET 8**. It produces **PBS-wire-compatible** backups (chunk protocol,
`fidx`/`didx` indexes, manifest, client-side encryption) so snapshots are listed, verified, and
restored by an unmodified Proxmox Backup Server.

> ## ⚠️ Not affiliated with Proxmox
>
> This is an **independent, community-maintained** project. It is **not produced by, endorsed by,
> or affiliated with Proxmox Server Solutions GmbH** in any way. **"Proxmox" and "Proxmox Backup
> Server" are trademarks of Proxmox Server Solutions GmbH**, used here only to describe
> interoperability. This client is a clean-room reimplementation of the wire protocol; it ships no
> Proxmox code. No warranty — see the licence. Licensed **AGPL-3.0-or-later**.
>
> The CLI verbs, flags, and (planned) installer/service intentionally **resemble the upstream
> `proxmox-backup-client`** for familiarity, but this project is separate and its repository is not
> aligned with upstream's.

## Why this exists

Proxmox ships no Windows client. The official `proxmox-backup-client` is Rust and deeply
Linux-coupled (the `pxar` format encodes Unix metadata; file-restore uses FUSE; the build needs
`libsystemd`/`libacl`/`fuse3`), and it has **no VSS / Windows-image support** at all. Rather than
port a Linux binary, this project is a clean-room Windows implementation on a stack where the
Windows backup surface — **VSS, the SQL VSS Writer, VDI, WMI (all COM)** — is first-class.

The sibling repo [`pbs-client`](https://github.com/ciroiriarte/pbs-client) covers the Linux
(RPM/DEB) packaging and explicitly scopes the Windows client out; this repo is that Windows client.

## Scope

| Capability | Status |
|---|---|
| File / directory backup (pxar) + restore | **working (M1)** — verified against PBS 4.2.5 |
| CLI (`backup` / `restore` / `snapshots`) | **working (M1)** |
| Client-side encryption (AES-256-GCM, `--keyfile`-compatible) | **working (M2)** — official client decrypts it |
| VSS application-consistent snapshots (`backup --vss`) | **working (M2)** — captures open/locked files, verified on Windows Server 2022 |
| Incremental backups (known-chunk reuse) | planned (M2) |
| Full-disk / bare-metal image backup + restore (`backup --image`, FIDX) | **working (M3)** — VSS volume image, verified on Windows |
| Windows Service, scheduling, MSI packaging, code signing | planned (M4) |
| MSSQL application-aware backup (VSS Writer → SQL VDI/SMO) | planned (M5) |

The M1 core is proven end to end: a directory is encoded to `pxar`, chunked, uploaded over the
`proxmox-backup-protocol-v1` HTTP/2 session, and restored bit-for-bit — and the archives are valid
enough that the **official `proxmox-backup-client` restores them** and the PBS server `verify`s them
with zero errors.

## Usage

```sh
# repository: [AUTHID@]HOST[:PORT]:DATASTORE
export PBS_REPOSITORY='root@pam!token@pbs.example:store01'
export PBS_PASSWORD='<token-secret>'
export PBS_FINGERPRINT='aa:bb:...'      # for self-signed PBS certs

proxmox-backup-client backup root.pxar:/path/to/dir --ns my-namespace --backup-id my-host
proxmox-backup-client snapshot list --ns my-namespace
proxmox-backup-client restore host/my-host/1700000000 root.pxar /restore/target --ns my-namespace

# Windows, application-consistent backup via VSS (captures open/locked files):
proxmox-backup-client backup root.pxar:C:\Data --vss --ns my-namespace --backup-id my-host

# Windows, whole-volume image backup via VSS (FIDX), and restore to a raw image:
proxmox-backup-client backup disk0.img:C: --image --ns my-namespace --backup-id my-host
proxmox-backup-client restore host/my-host/1700000000 disk0.img /restore/disk0.img --image --ns my-namespace

# encrypted (interoperable with the official client's --keyfile):
#   proxmox-backup-client key create --kdf none key.json
proxmox-backup-client backup root.pxar:/path --ns ns --keyfile key.json
proxmox-backup-client restore host/my-host/1700000000 root.pxar /target --ns ns --keyfile key.json

# Windows, browse a file archive read-only via ProjFS (no full restore):
proxmox-backup-client mount host/my-host/1700000000 root.pxar C:\mnt\backup --ns my-namespace
proxmox-backup-client unmount C:\mnt\backup

# Windows, expose a restored image as a block device via WNBD (third-party driver):
proxmox-backup-client map host/my-host/1700000000 disk0.img --device pbs-disk0 --ns my-namespace
proxmox-backup-client unmap pbs-disk0
```

> `map` / `unmap` require the third-party **WNBD** driver (Cloudbase, LGPL-2.1). It
> is detected, not bundled — install it and see licensing details in
> [docs/WNBD.md](docs/WNBD.md).

## Architecture (summary)

- **Transport & format core** (`Pbs.Protocol`, `Pbs.Chunker`, `Pbs.Pxar`) — cross-platform,
  buildable and testable on Linux/macOS/Windows.
- **Windows integration** (`Pbs.Vss`, `Pbs.Service`, `Pbs.Gui`) — Windows-only (VSS via AlphaVSS,
  Windows Service host, GUI). Added from M2 onward.
- **Providers** (`Pbs.Providers`) — pluggable `IBackupProvider` implementations: filesystem,
  image, MSSQL, ….

## Repository layout

```
src/
  Pbs.Abstractions/   # core contracts (chunks, indexes, provider interfaces)
  Pbs.Protocol/       # HTTP/2 upgrade client, auth, TLS pinning, index/blob/manifest API
  Pbs.Chunker/        # content-defined + fixed chunking, SHA-256, zstd, encryption pipeline
  Pbs.Pxar/           # pxar-compatible encoder/decoder + catalog
  Pbs.Cli/            # command-line surface
tests/
  Pbs.Chunker.Tests/  # dedup / digest / chunk-boundary vectors
```

Windows-only projects (`Pbs.Vss`, `Pbs.Service`, `Pbs.Gui`, `Pbs.Providers`) are added in later
milestones and excluded from non-Windows builds via RID / `Condition` guards.

## Build

Requires the **.NET 8 SDK**.

```sh
dotnet build
dotnet test
```

The cross-platform core builds on any OS. Windows-specific projects require
`-r win-x64` on Windows.

## License

AGPL-3.0-or-later — see [`LICENSE`](LICENSE). This is an independent clean-room implementation;
it does not incorporate code from the GPLv3 Go clients (`tizbac/proxmoxbackupclient_go`,
`rdemsystems/NimbusBackupClient`), which are referenced only as protocol documentation.
