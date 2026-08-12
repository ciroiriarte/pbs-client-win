# ProjFS mount M2 — on-demand ranged reads: verification

Date: 2026-08-12. Target: VM144 (`10.2.0.148`, Server 2022 Desktop, ProjFS enabled).

M2 replaced the M1 whole-pxar hydration (restore to a temp file) with
`ReaderSession.OpenArchiveStream` — a seekable, ranged-read-backed stream.
`PxarIndexer` reads only header regions (skipping payloads via `Seek`); ProjFS
`GetFileData` fetches only the bytes each file needs. Code: commit `d62d377`.

## How this was verified
Codex's usage quota was exhausted (until 2026-08-17), so the VM144 build + test were
driven directly over `ssh root@bigiron.lan` + `qm guest exec 144` (source pushed via a
`systemd-run` HTTP server + in-guest `Invoke-WebRequest`; a base64'd PowerShell test
script run in-guest).

## Results — mount the existing pxar snapshot `host/projfs-test/1786506894`

| Check | Result |
| --- | --- |
| `Pbs.ProjFs` + `Pbs.Cli(net8.0-windows)` build | **succeeded, 0/0** |
| Mount startup line | `mounted projfs-test.pxar.didx … read-only (101071 bytes, on-demand)` |
| Recursive browse | full tree: `a.txt, b.bin, empty.dat, sub\, sub\c.txt, sub\deep\, sub\deep\d.txt` |
| SHA-256 `a.txt` | `8aa88971…bb6501` ✓ (matches source) |
| SHA-256 `sub\deep\d.txt` | `da48e03b…110507` ✓ |
| SHA-256 `empty.dat` | `e3b0c442…52b855` ✓ |
| **Temp `pbs-projfs-*.pxar` files during mount** | **0 — no hydration (truly on-demand)** |
| `unmount` → mount process exit | ✓ (#28 graceful stop) |
| New-file create in mount root | still succeeds — known limitation (#27) |

**Verdict: M2 works.** Content is served correctly via ranged reads with **no
whole-archive hydration** (zero temp files) — `mount` now fetches only what is read.

## Not covered here
- A large-file (128 MiB) on-demand demonstration (read only a few ranges of a big
  file) — the mechanism is proven above; the scale demo is deferred.
- Residual lifecycle items unchanged: #27 (deny new-file create — not viable via ACL),
  #28 (removing a *used* mount root still hits "provider temporarily unavailable").
