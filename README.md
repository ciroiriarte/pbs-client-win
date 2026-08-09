# pbs-client-win — native Windows Proxmox Backup client

A **Windows-native** backup client for [Proxmox Backup Server](https://pbs.proxmox.com), written
from scratch in **C# / .NET 8**. It produces **PBS-wire-compatible** backups (chunk protocol,
`fidx`/`didx` indexes, manifest, client-side encryption) so snapshots are listed, verified, and
restored by an unmodified Proxmox Backup Server.

> "Proxmox" is a trademark of Proxmox Server Solutions GmbH. This project is community-maintained
> and **not endorsed by Proxmox**. Licensed **AGPL-3.0-or-later**.

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
| VSS application-consistent snapshots | planned (M2) — **hard requirement** |
| Incremental backups (known-chunk reuse) | planned (M2) |
| Full-disk / bare-metal image backup + restore (`fidx`) | planned (M3) |
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

pbs-client-win backup root.pxar:/path/to/dir --ns my-namespace --backup-id my-host
pbs-client-win snapshots --ns my-namespace
pbs-client-win restore host/my-host/1700000000 root.pxar /restore/target --ns my-namespace

# encrypted (interoperable with the official client's --keyfile):
#   proxmox-backup-client key create --kdf none key.json
pbs-client-win backup root.pxar:/path --ns ns --keyfile key.json
pbs-client-win restore host/my-host/1700000000 root.pxar /target --ns ns --keyfile key.json
```

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
