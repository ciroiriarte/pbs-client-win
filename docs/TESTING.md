# Testing & validation environment

How to build and run the test suite, including the live integration tests against a real Proxmox
Backup Server and the Windows-only (VSS / image) tests on a build VM. Written so a fresh session can
pick this up and just run the tests.

## 0. Toolchain

Requires the **.NET 8 SDK**. On this Linux dev box it is installed under `~/.dotnet` (no root):

```sh
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# If missing: curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "$HOME/.dotnet"
```

## 1. Cross-platform unit tests (any OS, no server)

```sh
dotnet test PbsClientWin.sln          # Pbs.Chunker / Pbs.Protocol / Pbs.Pxar / Pbs.Cli tests
```

`Pbs.Vss` and `Pbs.Vss.Tests` are **Windows-only** and deliberately NOT in the solution, so the
Linux build stays `net8.0`-clean. The integration tests are in the solution but self-skip when the
PBS env vars are unset.

## 2. Live integration tests (against a real PBS)

Connection details/credentials live **outside the repo** in `~/.config/pbs-client-win/test.env`
(git-ignored, mode 600). Source it and run:

```sh
source ~/.config/pbs-client-win/test.env      # sets PBS_ENDPOINT/TOKEN_ID/TOKEN_SECRET/FINGERPRINT/DATASTORE/NAMESPACE
dotnet test tests/Pbs.Integration.Tests       # backup/restore, encryption, incrementals, image, catalog
```

The test PBS is **`pbs-01.lan`** (`10.2.0.211:8007`), datastore **`backup01`**, namespace
**`windows-test`**, API token **`root@pam!windows-test`** (DatastoreAdmin on `backup01`).
If the token secret is lost, regenerate on the server (you run this — it prints the secret once):

```sh
ssh root@pbs-01.lan proxmox-backup-manager user generate-token root@pam windows-test
ssh root@pbs-01.lan "proxmox-backup-manager acl update /datastore/backup01 DatastoreAdmin --auth-id 'root@pam!windows-test'"
```

### Manual CLI runs

Build once, then invoke the DLL (avoids the multi-target `dotnet run -f` prompt):

```sh
dotnet build src/Pbs.Cli -c Debug -f net8.0
DLL=src/Pbs.Cli/bin/Debug/net8.0/Pbs.Cli.dll
export PBS_REPOSITORY="root@pam!windows-test@pbs-01.lan:backup01" PBS_PASSWORD="$PBS_TOKEN_SECRET"
dotnet "$DLL" snapshot list --ns windows-test --fingerprint "$PBS_FINGERPRINT"
```

Clean up accumulated test snapshots: `dotnet "$DLL" prune host/<id> --keep-last 1 --ns windows-test`.

## 3. Windows tests (VSS + image) on the build VM

The Windows-only tests need real Windows + LAN access to `pbs-01`, provided by a VM on the Proxmox
VE host **`bigiron.lan`** (`root@bigiron.lan`, PVE 9). VM details are recorded in
`~/.config/pbs-client-win/test.env` (VMID, IP). The VM is a clone of the cloudbase-init Windows
Server 2022 template (`qm` VMID 9022) with the QEMU guest agent running, on VLAN 100, .NET 8 SDK at
`C:\dotnet`. Drive it **headlessly** via the guest agent (no RDP):

```sh
# run a PowerShell script in the guest (encode UTF-16LE base64 to avoid quoting):
ssh root@bigiron.lan 'B64=$(iconv -f UTF-8 -t UTF-16LE /tmp/step.ps1 | base64 -w0); \
  qm guest exec <VMID> --timeout 300 -- powershell.exe -NoProfile -NonInteractive -EncodedCommand "$B64"'
```

Source delivery (the repo has no public remote by default): zip the working tree, `scp` to
`bigiron:/tmp/pbs-src.zip`, serve it from the host and pull it into the guest:

```sh
# on bigiron: serve /tmp over the LAN (10.2.0.210) for the guest to fetch
ssh root@bigiron.lan 'systemd-run --unit=pbs-http --collect python3 -m http.server 8088 --directory /tmp'
# in the guest (PowerShell): Invoke-WebRequest http://10.2.0.210:8088/pbs-src.zip -OutFile $env:TEMP\s.zip;
#   Expand-Archive to C:\pbs; then: C:\dotnet\dotnet.exe test tests\Pbs.Vss.Tests
```

Windows test env in the guest: same PBS_* vars (use `PBS_ENDPOINT=https://10.2.0.211:8007/`), plus
`IMG_VOLUME=X:` for the volume-image test (the harness makes a small VHD via `diskpart`). Reference
`step*.ps1` scripts from prior runs live under the session scratchpad; the flow is: download+extract
source → `dotnet test tests\Pbs.Vss.Tests` (VSS smoke + VSS→PBS E2E + volume image E2E).

## 4. Interop checks (ground truth)

Correctness is validated against the official `proxmox-backup-client` on `pbs-01`, e.g.:
`proxmox-backup-client catalog dump <snap> --ns windows-test`, `... restore ... --keyfile`,
`proxmox-backup-manager verify backup01`. These are the authority for wire-format correctness.
