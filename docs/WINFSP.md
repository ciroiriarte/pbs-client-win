# WinFsp dependency (`mount --provider winfsp`)

The `mount` command exposes a pxar archive as a read-only, on-demand browsable
filesystem. On the Windows client and on Server-with-Desktop-Experience this
uses **ProjFS** (built into Windows). On **Windows Server Core**, where ProjFS
is not available, `mount` instead uses **WinFsp** — the
[Windows File System Proxy](https://winfsp.dev/) — as a second mount engine.
See `docs/WNBD.md` for the parallel case of the `map`/`unmap` block-device
commands, which depend on WNBD instead.

## What WinFsp is

- A **third-party**, open-source Windows filesystem-in-userspace framework by
  **Bill Zissimopoulos**, licensed **GPLv3 with the WinFsp FLOSS exception**.
  Homepage / installer: **https://winfsp.dev/**.
- A properly **signed kernel driver** (`winfsp-x64.sys`) plus a native DLL
  (`winfsp-x64.dll`) and a **.NET binding**, the managed assembly
  `winfsp-msil.dll` (namespace `Fsp`), distributed as the NuGet package
  **`winfsp.net`** (pinned to `2.1.25156`, matching the latest **stable** WinFsp
  release, v2.1). The NuGet package is current, not a stale mirror — it supplies
  the managed binding our code links against; the native driver + `winfsp-x64.dll`
  are provided by the installed WinFsp (detect-and-require).
- Because the kernel driver is properly signed, WinFsp loads cleanly under
  Secure Boot. There is no signing saga here comparable to WNBD's (see
  `docs/WNBD.md`).

## Why the mount needs it on Server Core

ProjFS is a Windows optional feature (`Client-ProjFS`) that is only present on
editions with the Desktop Experience. It is confirmed **unavailable on Server
Core**: on Server 2022 Core (VM143), `Get-WindowsOptionalFeature` lists 245
features and none of them is ProjFS, and
`Enable-WindowsOptionalFeature -FeatureName Client-ProjFS` fails with
`Feature name Client-ProjFS is unknown`. Server Core is a common deployment
target for PBS clients, so `mount` needs a second engine that works there —
WinFsp fills that gap.

## Detect-and-require (why we don't bundle the native driver)

This client **does not bundle or redistribute the WinFsp kernel driver or
native DLL**. The `winfsp.net` NuGet package we reference supplies only the
**managed** binding (`winfsp-msil.dll`); it is used the same way any other
library dependency is — compiled against and referenced, not shipped as a
separate out-of-process CLI the way WNBD's `wnbd-client.exe` is.

The **native** components — `winfsp-x64.dll` and the `winfsp-x64.sys` driver —
still must be installed on the target machine. `mount --provider winfsp` (and
`--provider auto` on Server Core) *detects* whether WinFsp is installed and,
if it isn't, prints install guidance pointing back here. Detection checks, in
order:

- the native DLL at `%ProgramFiles(x86)%\WinFsp\bin\winfsp-x64.dll`,
- the FSD driver at `%SystemRoot%\System32\drivers\winfsp-x64.sys`, and
- the registry value `InstallDir` under
  `HKLM\SOFTWARE\WOW6432Node\WinFsp`, which WinFsp's installer records.

Because we redistribute no native WinFsp binaries, there are no bundling
obligations on this project for the native side either — see Licensing below.

## Installing WinFsp

1. Download the installer from **https://winfsp.dev/**.
2. Install **as Administrator** — installing a kernel driver requires
   elevation, and `mount --provider winfsp` also needs Administrator and the
   driver loaded to attach a mount.
3. Verify: after install, `%ProgramFiles(x86)%\WinFsp\bin\winfsp-x64.dll`
   should exist and `winfsp-x64.sys` should be present under
   `%SystemRoot%\System32\drivers`. `mount --provider winfsp` (or
   `--provider auto`) will detect it automatically on the next run.

## Licensing — how WinFsp relates to this (AGPL-3.0) client

This client is **AGPL-3.0**; WinFsp is **GPLv3 with the WinFsp FLOSS
exception**. The FLOSS exception explicitly permits linking WinFsp (via its
.NET binding or native API) from software distributed under another free
software license, which makes it compatible with an AGPL-3.0 client under
**AGPL-3.0 §13**. We keep the same **arm's-length** posture as with WNBD:

- We reference `winfsp.net` as a normal library dependency — the managed
  binding is linked into our managed assembly, not statically merged with the
  native driver.
- We ship **no WinFsp binaries** — native or managed — so no GPLv3/FLOSS
  redistribution terms apply here. The native driver and DLL are installed
  independently by the user from https://winfsp.dev/, and the `winfsp.net`
  NuGet package is restored at build time, not vendored into our source tree.
- If this project ever bundles WinFsp binaries in an installer, that decision
  needs its own review of the FLOSS exception's terms, mirroring the WNBD
  bundling notes in `docs/WNBD.md`.

## Credibility: who else uses WinFsp

WinFsp is a widely used, mature dependency in the Windows filesystem-in-
userspace space — it is the FUSE-for-Windows layer behind projects such as
**rclone**, **sshfs-win**, and **Cryptomator**.

## The `--provider` flag

`mount` accepts `--provider projfs|winfsp|auto` (default `auto`). `auto`
selects ProjFS if available, else WinFsp if installed, else prints guidance.
A WinFsp mount target may be a **drive letter** (e.g. `X:`) or a directory;
ProjFS mounts remain directory-only. `unmount <target>` stops either engine.
