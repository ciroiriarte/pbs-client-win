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
- **M2 — Encryption, incremental, VSS.**
  - **Client-side encryption** (AES-256-GCM, keyed digests, key fingerprint) — interoperable with
    the official client's `--keyfile`; unprotected and scrypt-protected keyfiles.
  - **Incremental backups** — known-chunk reuse from the previous snapshot.
  - **VSS** (application-consistent snapshots via the VSS requestor, COM) — verified on real
    Windows Server 2022, including locked-file capture; wired into the CLI via `--vss`.
- **M3 — Full-disk image backup + restore.** `fixed_index`/`fixed_chunk` (FIDX); VSS-snapshotted
  raw volume read; image restore. Wired into the CLI via `--image`; VSS image backup E2E verified.

## CLI parity with proxmox-backup-client

Done: `backup` (file/pxar, image/fidx, `--vss`, `--exclude`, incremental, encryption),
`restore` (pxar extract, `--image`), `snapshot list/forget/notes/protected`,
`prune`, `namespace create/list/delete`, `status`, `snapshot list --output-format json`,
`key create` and encrypted backup with `--keyfile` (kdf none + scrypt) — all verified
interoperable with the official client where applicable.

Also done: **catalog** (`.pcat1` upload + `catalog dump`, byte-compatible — the official
`proxmox-backup-client catalog dump` reads our catalog and vice versa), **selective restore**
(`restore --pattern <glob>`), and **benchmark** (local crypto/compression/hashing throughput plus a
TLS upload leg with `--repository`, using the server's `benchmark=true` discard mode; `--output-format
json`) — verified on Linux and the Windows build VM, and against a live PBS.

Remaining vs the official client:
- **catalog shell** (interactive browse) — the on-disk catalog is done; only the interactive REPL is missing.
- **mount / unmount** — done on Windows as a **read-only, on-demand pxar browse** with a
  **dual mount engine** (`--provider projfs|winfsp|auto`): **ProjFS** on the client /
  Server-with-Desktop-Experience (zero-install), **WinFsp** on **Server Core** (where ProjFS is
  unavailable) and for drive-letter targets. Both engines share one backing model (catalog + pxar
  index + ranged reader). See `docs/MOUNT.md` (read-only semantics per engine) and `docs/WINFSP.md`.
- **map / unmap** — block-device mechanism is Linux-specific; the Windows equivalent attaches a
  restored image as a block device via WNBD (see `docs/WNBD.md`).

Also done: **change-owner** (POST `change-owner` with backup-type/backup-id/new-owner), **task**
`list`/`log`/`stop` (`/nodes/{node}/tasks`, node defaults to `localhost`), **login/logout**
(ticket + CSRF cookie auth via `/access/ticket`, with TOTP/recovery **2FA**; ticket cached per
host+user, used for both the JSON API and the backup/reader upgrade), and **advanced key management**
— `key change-passphrase`, `paperkey`, and master-key/RSA escrow (`create-master-key`,
`import/show-master-pubkey`, and `backup --master-pubkey-file` storing an `rsa-encrypted.key.blob`,
RSA PKCS#1 v1.5 over the unprotected keyfile JSON). API-token auth remains the recommended path for
unattended backups. All verified against a live PBS and the official client — re-wrapped keys decrypt
under the new passphrase (and reject the old), the 2FA path with a real TOTP-enrolled user, and the
master key recovers the exact data key from our escrow blob.

## Next

- **M4 — Service, scheduling, packaging, code signing.** _(in progress)_
  - **Done (verified on Linux CI):** `Pbs.Service` — a .NET Generic Host worker that schedules
    backup jobs from a JSON config (cron via Cronos), launches the verified `proxmox-backup-client` CLI as a
    subprocess (secrets passed via child-process env, never argv), serializes per repository+backup-id,
    then runs `prune` for retention. Sub-commands `run` / `install` / `uninstall` / `validate-config`.
    Builds + 5 unit tests green; `validate-config` round-trips the sample config.
  - **Done (validated on Windows Server 2022):** WiX v4 MSI (`installer/`) — installs the CLI +
    service (self-contained single-file, no .NET runtime prerequisite), registers the Windows
    service (delayed auto-start), adds it to PATH, and adds opt-out (`AV_EXCLUSIONS`) Windows
    Defender process/path exclusions (issue #9). Verified end to end on a real Server 2022 guest:
    **silent `msiexec /i … /qn /norestart` returns 0** (unattended-template compatible, e.g.
    ciroiriarte/pve-tools), the service starts and idles cleanly with no config, and **`/x /qn`
    uninstall returns 0** with full cleanup (service, files, PATH, and Defender exclusions removed).
  - **Code signing — apply for a free open-source certificate via SignPath.org:
    https://signpath.org/apply** (SignPath provides free code signing + certificates to OSS
    projects). Signing is the primary fix for the AV/SmartScreen false positives that plague the
    existing community Windows ports; also avoid packing (no UPX) and submit false-positive reports.
    Signing pipeline + AV-exclusion guidance drafted in `docs/SIGNING.md`, `docs/ANTIVIRUS.md`, and
    `.github/workflows/release-signing.yml` (build → sign exes → package MSI → sign MSI → release).
- **M5 — MSSQL application-aware backup.** VSS SQL Writer (tier 1) → SQL VDI/SMO streaming backup
  (tier 2) for full/differential/log + point-in-time restore.

## Known follow-ups

- Windows metadata mapping in `pxar` (POSIX mode/uid/gid are currently synthesized; map Windows
  ACLs/security descriptors) — revisit around M2/M3.
- Streaming pxar/chunking for large archives (currently buffers the archive in memory).
- Manifest signing (backups are encrypted but the manifest is not signed; official restore accepts
  this today).
- Defense-in-depth on restore: refuse to follow pre-existing on-disk symlinks during extraction.

## Operational hardening (TODO — mostly M4 install/design)

- **Multi-VSS-client coordination.** *Done* (single-instance + external back-off): a system-wide named
  semaphore serializes our own instances, and snapshot creation retries with bounded back-off when VSS
  reports a set already in progress from another requestor (PVE host backup via the QEMU guest agent, or
  an app's VSS writer). Scheduling guidance and the contention model are in `docs/VSS-COORDINATION.md`.
  Still open: proactively detecting an in-progress host backup, and SQL-writer component selection (with
  the MSSQL work, #2). Ref: https://pve.proxmox.com/wiki/VM_Backup_Consistency
- **Antivirus on-access exclusions.** *Done*: the MSI adds/removes Microsoft Defender exclusions (the two
  processes plus the install and ProgramData paths; toggle with `AV_EXCLUSIONS=0`), and
  `docs/ANTIVIRUS.md` documents manual/GPO setup plus exclusion steps for CrowdStrike, SentinelOne,
  Sophos, and ESET. Compounds with the code-signing/SmartScreen work (SignPath).

## Test matrix (TODO)

Once VSS + packaging land, test across the Windows templates available in the test PVE:
- Versions: **Windows Server 2019 / 2022 / 2025**
- Editions: **Core** and **Desktop Experience**
- Locales: **en-US** and **es-ES** (Korean/ko-KR dropped — no template available)

Locale coverage matters for path/encoding correctness in pxar (non-ASCII filenames).
