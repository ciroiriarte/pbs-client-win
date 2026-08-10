# NBD Implementation Report

## Files Added/Changed

- `src/Pbs.Nbd/Pbs.Nbd.csproj`
- `src/Pbs.Nbd/IBlockDeviceSource.cs`
- `src/Pbs.Nbd/ReaderSessionBlockSource.cs`
- `src/Pbs.Nbd/NbdServer.cs`
- `tests/Pbs.Nbd.Tests/Pbs.Nbd.Tests.csproj`
- `tests/Pbs.Nbd.Tests/NbdServerTests.cs`
- `tests/Pbs.Integration.Tests/NbdIntegrationTests.cs`
- `tests/Pbs.Integration.Tests/Pbs.Integration.Tests.csproj`
- `src/Pbs.Cli/Program.cs`
- `src/Pbs.Cli/Pbs.Cli.csproj`
- `PbsClientWin.sln`

## Validation

- `/home/ciro/.dotnet/dotnet build PbsClientWin.sln -c Debug`
  - PASS: build succeeded, 0 warnings, 0 errors.
- `/home/ciro/.dotnet/dotnet test tests/Pbs.Nbd.Tests`
  - PASS: 7 passed, 0 failed, 0 skipped, 7 total.
- `/home/ciro/.dotnet/dotnet test tests/Pbs.Protocol.Tests`
  - PASS: 54 passed, 0 failed, 0 skipped, 54 total.
- `/home/ciro/.dotnet/dotnet test tests/Pbs.Cli.Tests`
  - PASS: 8 passed, 0 failed, 0 skipped, 8 total.
- `/home/ciro/.dotnet/dotnet test tests/Pbs.Integration.Tests --no-build -c Debug`
  - PASS/SKIPPED: 0 passed, 0 failed, 9 skipped, 9 total. The new NBD integration test is gated by `PbsTestServer` like the existing live-PBS tests.
- `/home/ciro/.dotnet/dotnet run --project src/Pbs.Cli/Pbs.Cli.csproj --framework net8.0 --no-build -c Debug -- --help | rg "map|unmap"`
  - PASS: usage shows both `map` and `unmap`.

Note: the literal combined command `/home/ciro/.dotnet/dotnet test tests/Pbs.Protocol.Tests tests/Pbs.Cli.Tests` fails in this SDK with `MSB1008: Only one project can be specified`; the two projects were run separately and both passed.

## Deviations

- `--cache-size` was not added. `ReaderSession.OpenAsync` and the hardcoded 256 MiB chunk cache remain unchanged to avoid expanding the change beyond the NBD feature.
- The NBD transmission loop is sequential per connection by design for this MVP. This preserves reply ordering; out-of-order pipelining can be added later.
- TRIM and WRITE_ZEROES are rejected with EPERM and no data is read for them because standard NBD requests for those commands do not carry a payload. WRITE payloads are drained before EPERM.

## How To Use

Serve an image archive read-only over localhost:

```bash
proxmox-backup-client map host/myhost/1780000000 disk0 --repository 'root@pam!token@pbs.example:store01'
```

Choose bind, port, and export name:

```bash
proxmox-backup-client map host/myhost/1780000000 disk0 --repository 'root@pam!token@pbs.example:store01' --bind 127.0.0.1 --port 10809 --export-name disk0.img.fidx
```

Attach automatically on Windows when `wnbd-client` is on `PATH`:

```powershell
proxmox-backup-client map host/myhost/1780000000 disk0 --repository "root@pam!token@pbs.example:store01" --device pbs-disk0
```

Manual attach command for the Windows test VM 143 after starting the server:

```powershell
wnbd-client map pbs-disk0 127.0.0.1 --port 10809 --export-name disk0.img.fidx
```

Unmount on Windows:

```powershell
proxmox-backup-client unmap pbs-disk0
```
