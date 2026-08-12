# WNBD dependency (`map` / `unmap`)

The `map` / `unmap` commands expose a restored PBS image as a **Windows block
device**. Windows has no `/dev/loop`, so this is done with **WNBD** — the
[Windows Network Block Device](https://github.com/cloudbase/wnbd) driver.

## What WNBD is

- A **third-party**, open-source project by **Cloudbase Solutions Srl**, licensed
  **LGPL-2.1**.
- A **kernel-mode Storport miniport driver** (`wnbd.sys`) plus the `wnbd-client`
  CLI and `libwnbd.dll`. Our client talks to a WNBD device over a localhost NBD
  connection (see `map`).
- **Not** a Windows component — it must be installed separately.

## Detect-and-require (why we don't bundle it)

This client **does not bundle or redistribute WNBD**. It *detects* WNBD and, if
absent, tells you how to install it (that guidance is the message `map`/`unmap`
print, and it points back here). Because we redistribute nothing, there are **no
LGPL-2.1 redistribution obligations** on this project.

## Installing WNBD

WNBD ships inside the **Ceph for Windows** package, which bundles the driver +
`wnbd-client.exe` + `libwnbd.dll`:

1. Download from **https://cloudbase.it/ceph-for-windows/**.
2. Install **as Administrator** (installing a kernel driver requires elevation;
   `map --device` / `unmap` also need Administrator and the driver loaded).
3. Ensure the **Visual C++ 2015–2022 x64 redistributable** is present —
   `libwnbd.dll` depends on it. If it's missing, `wnbd-client` fails to start with
   `0xC0000135` (STATUS_DLL_NOT_FOUND). Install
   [`vc_redist.x64.exe`](https://aka.ms/vs/17/release/vc_redist.x64.exe) as
   Administrator. Because WNBD is detected, not bundled (see below), this client
   does not ship the redistributable — instead `map`/`unmap` recognize the
   `0xC0000135` failure and print this same guidance.
4. Verify: `wnbd-client.exe -v` and `wnbd-client.exe list` should run without error.

Already have `wnbd-client.exe` somewhere non-standard? Point the client at it with
`--wnbd-client-path <path>`. Otherwise the client searches `--wnbd-client-path`,
`PATH`, `C:\wnbd`, and the Ceph install dir.

> Driver signing note: WNBD is a kernel driver and must be signed to load on a
> standard machine. Cloudbase ships a signed build in the Ceph MSI. For dev/test
> with an unsigned/self-signed driver, a test-signing machine is required.

## Licensing — how WNBD relates to this (AGPL-3.0) client

This client is **AGPL-3.0**; WNBD is **LGPL-2.1**. They stay **arm's length**:

- We interact with WNBD **only through its separate `wnbd-client` process and the
  standard NBD/device interface** — we **never statically link** WNBD code into
  our binary. This is mere aggregation / dynamic use, not a combined work.
- We ship **no WNBD code or binaries**, so no LGPL redistribution terms apply here.

### If WNBD is ever bundled in the MSI

If a future installer bundles WNBD (tracked in #13/#23), it must:

- ship the **unmodified, Cloudbase-signed** `wnbd.sys` / `wnbd-client.exe` /
  `libwnbd.dll` (do not fork or modify — that would invalidate the signature and
  add obligations),
- include the **LGPL-2.1 license text**, and
- provide a **written offer for the corresponding source**, valid for at least
  three years (LGPL-2.1 §6(c)). The maintained WNBD source is at
  **https://github.com/ciroiriarte/wnbd** and the build/sign tooling at the
  **`wnbd-dist`** repo, and
- revisit chain-installing the **VC++ x64 redistributable** (#26): once we ship
  `libwnbd.dll` ourselves, its runtime becomes our responsibility rather than the
  user's.
