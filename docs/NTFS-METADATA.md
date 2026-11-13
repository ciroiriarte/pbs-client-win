# NTFS metadata in pxar — design (issue #3)

Status: **draft**, not implemented. Today the encoder writes a synthesized POSIX mode, uid/gid 0 and
no metadata records; the decoder skips XATTR/ACL/FCAPS/QUOTA records. A Windows restore therefore
loses owners, DACLs/SACLs, attributes, creation time and alternate data streams (ADS).

## Goal

Back up and restore, per file and directory:

| Windows metadata | Captured from |
|---|---|
| Security descriptor (owner, group, DACL, SACL) | `GetFileSecurityW` / `BackupRead` security stream |
| File attributes (read-only, hidden, system, archive, …) | `GetFileInformationByHandle` |
| Creation time (and change time) | `GetFileInformationByHandleEx(FileBasicInfo)` |
| Alternate data streams | `FindFirstStreamW` / `BackupRead` |

The metadata must survive a round trip through the **official client**: a backup of ours restored by
`proxmox-backup-client` on Linux, then backed up again by it, must still restore on Windows with
its ACLs. The metadata must also stay attached to its entry. That keeps selective restore, `mount`,
the catalog and metadata change detection working without a side index.

## Decision: Samba's xattr conventions, one XATTR record per item

pxar already has a per-entry `XATTR` record (name + value), and upstream's restore applies only
names that pass `proxmox_sys::fs::xattr::is_valid_xattr_name`
(`proxmox-sys/src/fs/xattr.rs`): `user.*`, `trusted.*`, `security.capability`, and explicitly
**`security.NTACL`, with the comment "samba saves windows ACLs there"**. Upstream's create path
(`pbs-client/src/pxar/create.rs`, `get_xattr_fcaps_acl`) captures the same set.

So we use the names and encodings Samba already uses (`librpc/idl/xattr.idl`):

| Item | xattr name | Encoding |
|---|---|---|
| Security descriptor | `security.NTACL` | NDR `xattr_NTACL` (version and hash type: see open question 1) |
| Attributes + creation time | `user.DOSATTRIB` | NDR `xattr_DOSATTRIB`; version 5 carries `attrib` and `create_time` |
| ADS `name` | `user.DosStream.name:$DATA` | raw stream bytes, as `vfs_streams_xattr` stores them (exact naming and trailing NUL to verify) |

Why not a sidecar file (the approach Salvanza takes: one gzipped JSON with SDDL strings at the
archive root)? It works only with the client that wrote it, it breaks as soon as a selective
restore or a mount skips the sidecar, and metadata change detection cannot see it per entry.

What this gives us, beyond our own round trip:

- **Official client on Linux** restores our archives with the xattrs intact on any filesystem with
  user xattrs; `security.NTACL` needs root (CAP_SYS_ADMIN), as with Samba.
- **Samba file servers.** A share backed up by the official client (it captures `security.NTACL`,
  `user.DOSATTRIB` and `user.DosStream.*` by default) restores on Windows through our client with
  real NTFS ACLs, attributes and streams. And our backups restored onto a Samba share show the
  original ACLs to SMB clients.

## Restore behaviour

**Ours on Windows.** Decode the XATTR records and apply them after the file data is written:
the streams (`CreateFileW` on `path:name`), then `SetFileInformationByHandle` (attributes, creation
time), then the security descriptor last (`SetFileSecurityW`; setting the owner needs
`SeRestorePrivilege`, the SACL `SeSecurityPrivilege`; they are already held when run elevated or as
the service). Read-only is applied last of all, because it blocks the other writes. Each of these is
optional (`--no-acl`, `--no-streams`, as for upstream's `--no-xattrs`/`--no-acls`); a missing
privilege downgrades to the DACL with a warning instead of failing the restore.

**Ours, archives without these records** (official-client Linux backups): unchanged — today's
behaviour, nothing applied.

**Official client.** `apply_xattrs` (`pbs-client/src/pxar/metadata.rs`) tolerates only
`EOPNOTSUPP`; it then stops applying xattrs and carries on. **Any other error fails the restore**, so
a value too large for the target filesystem is fatal: ext4 without `ea_inode` keeps all of an
inode's xattrs in one block (typically 4 KiB). This is the constraint that sets the ADS budget below.

## Limits

- **Security descriptors** are usually well under 1 KiB; one with many explicit ACEs can approach
  the ext4 block. We always store the SD (it is the point of the feature); an entry whose total
  xattr size exceeds the budget logs a warning naming the file.
- **ADS.** Most are tiny (`Zone.Identifier` ≈ 100 B). Phase 1 stores streams as xattrs while the
  entry's total xattr bytes stay within a budget (proposal: 3 KiB, so an official-client restore
  onto default ext4 still succeeds), and skips larger streams with a per-file warning plus a run
  total. Samba's own default cap is `XATTR_MAX_STREAM_SIZE` = 16 KiB. Storing large streams
  some other way is phase 2 (open question 3).
- **Encryption.** xattrs live in the metadata stream, which is encrypted with the rest of the
  archive. Nothing changes.

## Effect on the existing pieces

- **Encoder / split archives.** XATTR records go after `ENTRY`, before `FCAPS`/`ACL`/`QUOTA`, as
  upstream orders them. In split mode they belong to the `.mpxar` (metadata), which is written in
  full on every backup, so metadata-mode **payload reuse is unaffected**: an ACL change does not
  change the file's content or mtime and the payload stays valid. The reuse rule (size/mode/mtime)
  stays the same.
- **Decoder / indexer.** Keep the record bodies instead of skipping them; cap their sizes before
  allocating them, as for names and link targets (`ba4486c`). Proposal: 64 KiB per value, the Linux
  `XATTR_SIZE_MAX`.
- **Entry flags.** pxar's `ENTRY` has its own `flags` word, and upstream's default feature set
  includes `WITH_FLAG_HIDDEN`, `WITH_FLAG_SYSTEM` and `WITH_FLAG_ARCHIVE`
  (`pbs-client/src/pxar/flags.rs`). We write 0 there today. Set those bits from the Windows
  attributes as well as `user.DOSATTRIB`, so clients that ignore xattrs still see them.
- **Catalog.** No change (the catalog has no metadata).
- **Mount.** WinFsp can present the stored SD through its `GetSecurity` callback and the streams as
  named streams. ProjFS cannot, so it keeps today's ACLs. Optional, after restore works.
- **POSIX mode.** Still synthesized. Open question 4.

## Open questions (verify before coding)

1. **NTACL version and hash.** Which version and `hash_type` does current Samba's `acl_xattr`
   accept without checking against the POSIX ACL underneath? Proposal: version 4 with
   `XATTR_SD_HASH_TYPE_NONE`. Verify in `source3/modules/vfs_acl_common.c` and on a live share.
2. **NDR byte layout.** `security_descriptor *sd` is a unique pointer inside the union. Get
   reference bytes with `getfattr -n security.NTACL -e hex` from a real Samba share (and the same
   for `user.DOSATTRIB` and a stream) and make them the unit-test vectors, rather than writing the
   encoder from the IDL alone.
3. **Large ADS.** Do we skip them (phase 1), or encode them so they survive (hidden sibling entries,
   or a chunked `user.DosStream.*` series), or offer an opt-in flag that drops the ext4 budget?
4. **Mode bits.** Should read-only clear the `w` bits in the synthesized mode, so Linux restores
   look sane?
5. **Privileges on backup.** Capture SACLs only when `SeSecurityPrivilege` is available, or always
   attempt them and warn?

## Test plan

- Unit: encode and decode `xattr_NTACL`, `xattr_DOSATTRIB` and the stream names against the
  reference vectors from open question 2; the size caps; the ADS budget.
- Windows live (VM141): a tree with explicit and inherited ACEs, a SACL, every attribute, creation
  times, small and large ADS. Run backup → restore and compare with `icacls /save`, `Get-Item
  -Stream *` and `Get-ItemProperty`. Repeat with `--change-detection-mode metadata` and encrypted.
- Interop: official client restores ours on Linux, and `getfattr -d -m -` shows the three name
  families; official client backs that tree up again; ours restores it on Windows with the same
  `icacls` output.
- Samba: back up a Samba share with the official client (or `vfs_acl_xattr` + `vfs_streams_xattr`
  locally), restore it with ours on Windows, and check the ACLs match what an SMB client saw.
