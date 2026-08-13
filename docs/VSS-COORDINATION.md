# VSS coordination & scheduling

On a Windows guest, Volume Shadow Copy Service (VSS) can be driven by more than one requestor at a
time, and VSS only allows **one snapshot set in progress at a time**. Overlaps surface as
`VSS_E_SNAPSHOT_SET_IN_PROGRESS` ("a shadow copy operation is already in progress") and, at worst, can
disturb writer/log-chain state. The requestors that can collide:

1. **PVE host-level VM backup** — Proxmox VE freezes the guest for a consistent VM backup via the QEMU
   guest agent's `fs-freeze`, which on Windows invokes VSS. (See
   <https://pve.proxmox.com/wiki/VM_Backup_Consistency>.)
2. **This in-guest `pbs-client-win` backup** — takes its own VSS snapshot for file/image backup.
3. **Application-triggered backups** — e.g. the SQL Server VSS writer during a native SQL backup.

## What the client does

- **Single-instance guard (our own instances).** `VssSnapshotSource` holds a system-wide named
  semaphore (`Global\PbsClientWin.Vss.SingleInstance`) for the life of a snapshot, so two runs of *this*
  client cannot start overlapping snapshot sets. A second run waits briefly, then fails with
  "another VSS backup is already in progress on this system" rather than colliding.
- **External-contention back-off.** The semaphore cannot see *other* requestors (PVE host backup, SQL
  writer). If VSS reports a snapshot set already in progress when we call `DoSnapshotSet`, the client
  retries with bounded exponential back-off (`VssBusyRetry`) instead of failing the whole job on a
  transient overlap.

## Scheduling guidance

Back-off smooths brief overlaps, but it is not a substitute for scheduling:

- **Offset the in-guest schedule from the PVE host-level backup window.** If PVE backs the VM up at, say,
  02:00, schedule the in-guest `pbs-client-win` job well clear of it (before or after the host window),
  so the two are not both trying to freeze the guest at once.
- **Prefer one consistency mechanism per data set.** Backing a volume up *both* at the PVE host level and
  in-guest at the same time is redundant and contention-prone — pick the layer that matches your restore
  needs for that data.
- **Leave headroom for retries.** The back-off can add up to a couple of minutes to a job that hits an
  overlap; account for that in tight windows.

## Not yet covered

- **Detecting an in-progress host backup** proactively (rather than reacting to `VSS_E_...`) is a
  possible future refinement.
- **SQL VSS writer component selection** (so an application-aware SQL backup does not fight SQL's own
  VSS backups / log chain) is deferred to the MSSQL application-aware work (issue #2).
