# Roadmap

Status of the milestones from the project plan. Every "done" item is verified end to end against a
real Proxmox Backup Server (PBS 4.2.5), and where noted, against the official `proxmox-backup-client`.

## Design principles

- **Resemble upstream.** CLI verbs/flags, and the planned installer and Windows service, should
  follow the upstream `proxmox-backup-client` parameters and syntax as closely as practical, so the
  tool is familiar to existing PBS users. (The GitHub repository is deliberately *not* aligned with
  upstream's git; that is fine.)
- **Not affiliated with Proxmox.** Independent, community project; not endorsed by or affiliated
  with Proxmox Server Solutions GmbH. Clean-room reimplementation — ships no upstream code. The
  not-affiliated notice must remain prominent in the README, CLI `--help`, and installer.
- **Verify, don't assume.** Every format/protocol detail is checked against upstream source and a
  live server; interop is proven by round-tripping through the official client.

## Done

- **M0 — Foundation.** .NET 8 solution, cross-platform chunker core, CI.
- **M1 — File backup + restore.**
  - PBS wire formats (DataBlob, `fidx`/`didx`, manifest), content-defined chunker + dedup.
  - Backup session over the `proxmox-backup-protocol-v1` HTTP/2 upgrade; reader/restore session.
  - `pxar` encoder **and** decoder (official client restores our archives; we restore/round-trip).
  - CLI `backup` / `restore` / `snapshot list`.
- **M2 (partial).**
  - **Client-side encryption** (AES-256-GCM, keyed digests, key fingerprint) — interoperable with
    the official client's `--keyfile`.
  - **Incremental backups** — known-chunk reuse from the previous snapshot.

## Next

- **M2 — VSS (hard requirement).** Application-consistent snapshots via the VSS requestor (COM).
  Windows-only; write here, test on a Windows runner/CI.
- **M3 — Full-disk image backup + restore.** `fixed_index`/`fixed_chunk` (FIDX); VSS-snapshotted
  raw volume read; image restore. This is the format the VSS whole-disk path targets.
- **M4 — Service, scheduling, packaging, code signing.**
  - Windows Service host + scheduler + retention/prune; MSI (WiX) resembling upstream where sensible.
  - **Code signing — apply for a free open-source certificate via SignPath.org:
    https://signpath.org/apply** (SignPath provides free code signing + certificates to OSS
    projects). Signing is the primary fix for the AV/SmartScreen false positives that plague the
    existing community Windows ports; also avoid packing (no UPX) and submit false-positive reports.
- **M5 — MSSQL application-aware backup.** VSS SQL Writer (tier 1) → SQL VDI/SMO streaming backup
  (tier 2) for full/differential/log + point-in-time restore.

## Known follow-ups

- Windows metadata mapping in `pxar` (POSIX mode/uid/gid are currently synthesized; map Windows
  ACLs/security descriptors) — revisit around M2/M3.
- Streaming pxar/chunking for large archives (currently buffers the archive in memory).
- Password-protected keyfiles (scrypt/pbkdf2 KDF); today only `kdf none` keyfiles are read.
- Manifest signing (backups are encrypted but the manifest is not signed; official restore accepts
  this today).
- Defense-in-depth on restore: refuse to follow pre-existing on-disk symlinks during extraction.
