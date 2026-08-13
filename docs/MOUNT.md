# `mount` — read-only browse of a pxar archive

`mount` projects a backup's pxar archive as a **read-only, on-demand** filesystem
so you can browse and copy files out of a snapshot without a full restore. Content
is fetched lazily as it is read (ranged reads against the reader session — no
up-front hydration). `unmount <target>` tears the mount down.

```
proxmox-backup-client mount <type/id/time> <name.pxar> <dir|drive:> \
    --repository <repo> [--ns <ns>] [--provider projfs|winfsp|auto]
proxmox-backup-client unmount <dir|drive:>
```

## Two engines (`--provider`)

The mount is backed by a shared model (catalog + pxar index + ranged reader);
`IPbsMountProvider` lets either of two Windows engines serve it. `--provider`
defaults to `auto`.

| Engine | Where it fits | Target | Zero-install |
| --- | --- | --- | --- |
| **ProjFS** | Windows client / Server **with Desktop Experience** | directory only | Yes (built into Windows) |
| **WinFsp** | Windows **Server Core** (ProjFS unavailable) and drive letters | directory or drive letter (`X:`) | No — detect-and-require (`docs/WINFSP.md`) |

`auto` selects ProjFS if available, else WinFsp if installed, else prints install
guidance. On Server Core, `auto` resolves to WinFsp because ProjFS is not present.
See `docs/WINFSP.md` for the WinFsp dependency and licensing; `docs/WNBD.md` covers
the separate `map`/`unmap` block-device path.

## Read-only semantics

Both engines serve **existing backup content strictly read-only** — the projected
files cannot be overwritten, modified, deleted, or renamed. The engines differ only
in one edge case: creating a **brand-new** file under the mount.

### WinFsp — strictly read-only

The WinFsp volume denies every mutation: create, write, overwrite, delete, rename,
and metadata changes all return `STATUS_MEDIA_WRITE_PROTECTED`. Nothing can be
written anywhere in the mount. Use `--provider winfsp` when you need a hard
read-only guarantee.

### ProjFS — existing content read-only; new files are ephemeral scratch (issue #27)

ProjFS denies every mutation of projected content (delete / rename / hardlink /
overwrite are refused via the deniable pre-callbacks). It has **no deniable
pre-create callback** (`NewFileCreated` is a post-operation notification, and the
mapping covers the root *and all descendants*), so ProjFS cannot block a user from
creating a *new* file anywhere under the mount, and a directory deny-create ACL is
not usable — ProjFS hydrates placeholders in the browsing user's security context,
so any user-facing deny-create also denies enumeration and reads and breaks the
mount (verified on VM144, `docs/projfs-m1-gaps-report.md`).

The residual gap is benign: such a file lands only as **local scratch** under the
virtualization root. It is removed when `unmount` tears the root down, it is never
uploaded, and it never touches the backup — the datastore is read through a reader
session that has no write path at all. Existing backup content stays fully
read-only regardless.

If you need a mount that also refuses new-file creation, use **`--provider
winfsp`**, which is strictly read-only.
