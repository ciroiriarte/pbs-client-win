# WNBD + NBD E2E Report

Date: 2026-08-10  
Target VM: 143 `pbs-win-wnbd-test` (`10.2.0.147`)  
Result: **STOPPED at Step 3 / WNBD driver gate**

## Step Summary

| Step | Result | Evidence |
| --- | --- | --- |
| 1. Prepare live-PBS fixed-index image | PASS | Uploaded and listed `host/wnbd-e2e/1786388200`, archive `disk0.img.fidx`, size `67108864`. |
| 2. Publish and deploy CLI to VM143 | PASS | `C:\pbscli\proxmox-backup-client.exe --help` ran in-guest and showed `map` / `unmap`. |
| 3. Extract/install Cloudbase WNBD | FAIL | `wnbd-client.exe` exits `-1073741515` (`0xC0000135`) before `-v`, `install-driver`, or `list` can run. Static import check shows `libwnbd.dll` requires `VCRUNTIME140_1.dll`; VM143 does not have it. |
| 4. Serve + attach + verify bytes | NOT RUN | Stopped at Step 3 gate. |
| 5. Cleanup mapping | NOT NEEDED | No mapping was started; `C:\wnbd\map.log` is not present. |

## Step 1 - PBS Fixed-Index Image

Deterministic source image:

- Local file: `/tmp/pbs-wnbd-e2e/disk0.raw`
- Size: `67108864` bytes
- Pattern: `byte[i] = (byte)(((ulong)i * 2654435761UL) >> 13)`
- SHA-256: `f85505310ac55800e8f0eaf99106e28513c68366d240f98a5c50e8c9208bba25`

Upload evidence:

```text
uploaded=host/wnbd-e2e/1786388200
archive=disk0.img.fidx
size=67108864
sha256=f85505310ac55800e8f0eaf99106e28513c68366d240f98a5c50e8c9208bba25
stats=UploadStats { TotalChunks = 16, UploadedChunks = 1, ReusedChunks = 0 }
snapshot_count_for_wnbd-e2e=1
snapshot=host/wnbd-e2e/1786388200
```

The PBS snapshot was left in place for reuse, per instructions.

## Step 2 - CLI Publish/Deploy

Initial requested publish command failed because `src/Pbs.Cli` targets multiple frameworks:

```text
error NETSDK1129: The 'Publish' target is not supported without specifying a target framework.
```

Rerun used the same publish settings with `-f net8.0`:

```text
dotnet publish src/Pbs.Cli -f net8.0 -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o /tmp/pbscli-win
```

SMB from the local Linux box and from PVE to `//10.2.0.147/C$` timed out. The documented HTTP fallback succeeded using the PVE host as the temporary HTTP source (`10.2.0.210`), then the HTTP server was stopped.

CLI help gate output excerpt:

```text
proxmox-backup-client - Proxmox Backup client for Windows
version 1.0.0.0
...
  proxmox-backup-client map <type/id/time> <name.img> --repository <repo> [--ns <ns>] [--bind 127.0.0.1] [--port 10809] [--export-name <name>] [--device <instance>]
  proxmox-backup-client unmap <instance>
```

## Step 3 - Cloudbase WNBD

Source used:

- Cloudbase Ceph for Windows downloads page: `https://cloudbase.it/ceph-for-windows/`
- Current latest x64 link on that page: `https://cloudbase.it/downloads/ceph_squid.msi`

MSI administrative extract succeeded:

```text
msiexec_exit=0

FullName                                 Length
--------                                 ------
C:\ceph-extract\Ceph\bin\libwnbd.dll     111616
C:\ceph-extract\Ceph\bin\wnbd-client.exe 625152
C:\ceph-extract\Ceph\driver\wnbd.cat      11011
C:\ceph-extract\Ceph\driver\wnbd.inf       1811
C:\ceph-extract\Ceph\driver\wnbd.sys      86872
```

Signer evidence:

```text
Status            : Valid
StatusMessage     : Signature verified.
SignerCertificate : CN=Cloudbase Solutions Srl, O=Cloudbase Solutions Srl, L=Timisoara, C=RO
Issuer            : CN=SSL.com Code Signing Intermediate CA RSA R1, O=SSL Corp, L=Houston, S=Texas, C=US
Thumbprint        : E530EE278DEBA026C554FFF95790C66E4997061A
```

The signer certificate was imported into `LocalMachine\Root` and `LocalMachine\TrustedPublisher`.

WNBD client/driver gate output:

```text
--- version ---
version_exit=-1073741515
--- install-driver ---
install_exit=-1073741515
--- list ---
list_exit=-1073741515
--- pnputil wnbd ---
--- services wnbd ---
```

Retry after copying the full extracted `Ceph\bin` payload into `C:\wnbd`:

```text
--- version retry ---
version_exit=-1073741515
--- install-driver retry ---
install_exit=-1073741515
--- list retry ---
list_exit=-1073741515
--- pnputil wnbd retry ---
--- service/adapters retry ---
```

Static dependency evidence from the Cloudbase MSI payload:

```text
$ objdump -p /tmp/ceph-msi-local/Ceph/bin/wnbd-client.exe
DLL Name: KERNEL32.dll
DLL Name: WS2_32.dll
DLL Name: libwnbd.dll

$ objdump -p /tmp/ceph-msi-local/Ceph/bin/libwnbd.dll
DLL Name: KERNEL32.dll
DLL Name: MSVCP140.dll
DLL Name: SETUPAPI.dll
DLL Name: SHLWAPI.dll
DLL Name: VCRUNTIME140.dll
DLL Name: VCRUNTIME140_1.dll
DLL Name: WS2_32.dll
DLL Name: api-ms-win-crt-heap-l1-1-0.dll
DLL Name: api-ms-win-crt-math-l1-1-0.dll
DLL Name: api-ms-win-crt-runtime-l1-1-0.dll
DLL Name: api-ms-win-crt-stdio-l1-1-0.dll
DLL Name: api-ms-win-crt-string-l1-1-0.dll
DLL Name: newdev.dll
```

VM143 runtime evidence:

```text
False   # C:\Windows\System32\VCRUNTIME140_1.dll
False   # C:\wnbd\VCRUNTIME140_1.dll

Name                     Length
----                     ------
vcruntime140.dll          87880
vcruntime140_clr0400.dll  87296
```

Conclusion: `wnbd-client.exe` cannot start because `VCRUNTIME140_1.dll` is absent. Since the test instructions only allowed trivial package-manager installation for missing software and provided no documented VC runtime install fallback, execution stopped here.

## Step 4 Evidence

Not run. No `wnbd-client list`, `Get-Disk`, raw disk byte comparison, or read-only write attempt was possible because the WNBD client could not start and the driver was not installed.

Requested outputs:

```text
wnbd-client list: not available; command exited -1073741515 before producing output.
Get-Disk: not run; no WNBD disk was attached.
Byte comparison: not run; offsets checked: none.
C:\wnbd\map.log tail: C:\wnbd\map.log not present.
```

## Cleanup State

- No NBD server was started.
- No WNBD mapping was created.
- VM143 remains running.
- WNBD files remain in `C:\wnbd`.
- Cloudbase signer cert was imported into LocalMachine Root and TrustedPublisher.
- The PBS backup `host/wnbd-e2e/1786388200` was left in place.

## Resume Run - VC Runtime Fix and WNBD/NBD E2E

Date: 2026-08-10  
Target VM: 143 `pbs-win-wnbd-test` (`10.2.0.147`)  
Result: **PASS - WNBD + NBD end-to-end completed**

### Resume Step Summary

| Step | Result | Evidence |
| --- | --- | --- |
| A. Install VC++ 2015-2022 x64 redistributable | PASS | In-guest download of `https://aka.ms/vs/17/release/vc_redist.x64.exe` succeeded (`25635768` bytes). Silent install returned `0`. `Test-Path C:\Windows\System32\VCRUNTIME140_1.dll` returned `True`; file version `14.44.35211.0`. |
| B. Install WNBD driver | PASS | `wnbd-client.exe -v` started and printed `wnbd-client.exe: 0.7.0` / `libwnbd.dll: 0.7.0`. `install-driver C:\wnbd\wnbd.inf` returned `0`. `wnbd-client.exe list` returned cleanly. `pnputil /enum-drivers` shows `Original Name: wnbd.inf`, `Published Name: oem13.inf`, provider `SUSE LLC`, signer `Cloudbase Solutions Srl`, version `0.7.0.0`. |
| C. Serve + attach + verify bytes | PASS | Server exported `disk0.img.fidx` read-only at `127.0.0.1:10809`; WNBD mapped instance `pbs-e2e` as disk `1`; all sampled byte ranges matched expected pattern. |
| D. Cleanup | PASS | `proxmox-backup-client.exe unmap pbs-e2e` returned `0`; final `wnbd-client.exe list` was empty; final `Get-Disk` showed only the VM system disk. Driver and VC runtime were left installed; VM143 was left running; PBS snapshot was left in place. |

### WNBD Attach Evidence

`wnbd-client.exe list` while attached:

```text
Pid         DiskNumber  Nbd    Owner            InstanceName
3880        1           true   wnbd-client      pbs-e2e
```

`Get-Disk` while attached:

```text
Number FriendlyName       SerialNumber        Size OperationalStatus IsOffline IsReadOnly
------ ------------       ------------        ---- ----------------- --------- ----------
     0 QEMU QEMU HARDDISK              64424509440 Online                False      False
     1 WNBD WNBD_DISK     pbs-e2e         67108864 Offline                True       True
```

Note: the first detached launch served the NBD export but did not auto-attach because `wnbd-client.exe` was not on `PATH`. The run was stopped and relaunched with `C:\wnbd` prepended to the process environment. The final launch auto-attached successfully and is the run recorded above.

### Byte Verification

Raw device: `\\.\PhysicalDrive1`  
Logical sector size: `512` bytes  
Expected pattern: `byte[i] = (byte)(((ulong)i * 2654435761UL) >> 13)`

| Offset | Length | Match | Notes |
| ---: | ---: | --- | --- |
| `0` | `4096` | `true` | Direct synchronous `ReadFile` from `\\.\PhysicalDrive1` succeeded. |
| `1048573` | `4096` | `true` | Direct unaligned `ReadFile` returned `error=87 The parameter is incorrect`; verified the requested byte slice by reading the aligned physical envelope `offset=1048064`, `length=4608` from the same raw device. |
| `67104768` | `4096` | `true` | Direct synchronous `ReadFile` near end of disk succeeded. |

Read-only confirmation:

```text
WriteFile error=19 The media is write protected
```

### Map Log Tail

`C:\wnbd\map.log`:

```text
NBD export 'disk0.img.fidx' serving disk0.img.fidx (67108864 bytes) at 127.0.0.1:10809 read-only
mapped pbs-e2e
Press Ctrl-C to stop serving.
```

`C:\wnbd\map.err`:

```text
19:09:56.018 libwnbd.dll!NbdDaemon::ConnectNbdServer INFO Initializing NBD connection.
19:09:56.018 libwnbd.dll!NbdDaemon::ConnectNbdServer INFO Successfully connected to NBD server: 127.0.0.1.
19:09:56.018 libwnbd.dll!NbdNegotiate INFO NBD negotiation successful.
19:09:56.018 libwnbd.dll!NbdDaemon::TryStart INFO Retrieved NBD flags: 7. Read-only: 1, TRIM enabled: 0, FLUSH enabled: 1, FUA enabled: 0.
19:09:56.033 libwnbd.dll!WnbdCreate INFO Mapped device. Connection id: 2. Bus: 0, target: 0, lun: 0.
19:09:56.033 libwnbd.dll!NbdDaemon::TryStart INFO NBD mapping initialized successfully.
19:09:56.033 libwnbd.dll!WnbdRunNbdDaemon INFO NBD mapping created, running as a daemon. Press CTRL-C to stop or use 'wnbd-client unmap'.
19:09:56.033 libwnbd.dll!NbdDaemon::Wait INFO Waiting for the WNBD dispatchers.
19:14:07.614 libwnbd.dll!WnbdRemove INFO Unmapping device pbs-e2e.
19:14:07.614 libwnbd.dll!NbdDaemon::Wait INFO WNBD dispatchers stopped.
19:14:07.614 libwnbd.dll!WnbdRemove INFO Unmapping device pbs-e2e.
19:14:07.614 libwnbd.dll!WnbdIoctlShow INFO Could not find the specified disk.
19:14:07.614 libwnbd.dll!NbdDaemon::DisconnectNbd INFO Removing NBD connection.
19:14:07.614 libwnbd.dll!NbdDaemon::DisconnectNbd INFO NBD connection closed.
19:14:07.614 libwnbd.dll!NbdDaemon::Wait INFO Waiting for the WNBD dispatchers.
19:14:07.614 libwnbd.dll!WnbdWaitDispatcher INFO Dispatcher stopped, waiting for any remaining dispatcher threads.
19:14:07.614 libwnbd.dll!NbdDaemon::Wait INFO WNBD dispatchers stopped.
19:14:07.614 libwnbd.dll!NbdDaemon::~NbdDaemon INFO Waiting for NBD reply dispatcher thread.
19:14:07.614 libwnbd.dll!RecvExact INFO Request canceled.
19:14:07.614 libwnbd.dll!NbdDaemon::NbdReplyWorker INFO Connection closed.
19:14:07.614 libwnbd.dll!NbdDaemon::~NbdDaemon INFO NBD reply dispatcher stopped.
```

### Final Cleanup Evidence

`unmap`:

```text
UNMAP_EXIT=0
```

Final `wnbd-client.exe list`:

```text
Pid         DiskNumber  Nbd    Owner            InstanceName
```

Final `Get-Disk`:

```text
Number FriendlyName       SerialNumber        Size OperationalStatus IsOffline IsReadOnly
------ ------------       ------------        ---- ----------------- --------- ----------
     0 QEMU QEMU HARDDISK              64424509440 Online                False      False
```
