# M4 build context (for delegated implementation)

You are implementing part of **M4** for `pbs-client-win`, an independent, clean-room .NET 8
reimplementation of the Proxmox Backup Server client for Windows. **Not affiliated with Proxmox.**
This context is authoritative — match it exactly so your output drops into the repo and builds.

## Repo facts

- Solution: `PbsClientWin.sln` at repo root. `Directory.Build.props` sets shared props.
- Language: C# / .NET 8. `ImplicitUsings=enable`, `Nullable=enable`.
- Existing projects under `src/`: `Pbs.Abstractions`, `Pbs.Chunker`, `Pbs.Protocol`, `Pbs.Pxar`,
  `Pbs.Vss`, `Pbs.Cli`. Tests under `tests/` (xUnit).
- The CLI project `src/Pbs.Cli` produces the executable **`proxmox-backup-client`** (mirrors the upstream client name). It multi-targets
  `net8.0` on Linux/macOS and `net8.0;net8.0-windows` on Windows (VSS is Windows-only, gated on the
  `WINDOWS` symbol / `net8.0-windows` TFM).
- CLI verbs already shipped and verified against a live PBS 4.2.5:
  `backup`, `restore`, `snapshot list|forget|notes|protected`, `prune`, `namespace create|list|delete`,
  `status`, `catalog dump`, `key create`, `version`.
- CLI reads connection config from flags **or** environment variables:
  - `--repository` / `PBS_REPOSITORY` — form `user@realm!token@host:datastore`
  - `--password` / `PBS_PASSWORD` — API token secret
  - `--fingerprint` / `PBS_FINGERPRINT` — TLS cert fingerprint
  - `--ns` / `PBS_NAMESPACE`
  - `--keyfile` / `PBS_KEYFILE`, `--key-password` / `PBS_ENCRYPTION_PASSWORD`
- Backup invocation shape:
  `proxmox-backup-client backup <name.pxar>:<dir> [more sources] --repository <repo> [--ns <ns>] [--backup-id <id>] [--vss] [--no-incremental]`
  Image backup: `proxmox-backup-client backup <name.img>:<volume> --image --repository <repo> [--ns <ns>]`
- Prune invocation shape:
  `proxmox-backup-client prune <type/id> --keep-last N [--keep-daily N --keep-weekly N ...] [--dry-run] --repository <repo> [--ns <ns>]`

## Design principles (must hold)

- **Resemble upstream `proxmox-backup-client`** in verbs/flags where practical.
- **Not affiliated with Proxmox** — the not-affiliated notice must remain prominent in installer UI,
  `--help`, and README. Ships no upstream code.
- **Verify, don't assume.** Prefer designs that reuse the already-verified CLI over reimplementing
  backup logic.

## M4 architecture decision (do NOT deviate)

The service is a **scheduler + supervisor that launches the existing `proxmox-backup-client` CLI as a
subprocess**. It does NOT re-implement backup/prune/protocol logic. This keeps the verified code
path untouched and isolates the service. Secrets are passed to the child process via environment
variables (never on the command line, which is visible in process listings).
