# ProjFS Mount M1 Report

## Files Added

- `src/Pbs.Pxar/PxarIndexer.cs`
  - Cross-platform seekable pxar scanner.
  - Records directory, file, and symlink entries.
  - Records absolute payload offsets and lengths for regular files without reading payload bytes.
- `tests/Pbs.Pxar.Tests/PxarIndexerTests.cs`
  - Builds pxar fixtures through `PxarEncoder`.
  - Verifies root files, nested files, empty files, payload advancement, and symlink indexing.
- `src/Pbs.ProjFs/Pbs.ProjFs.csproj`
  - Windows-only `net8.0-windows` project.
  - References `Microsoft.Windows.ProjFS` 2.1.0, `Pbs.Protocol`, and `Pbs.Pxar`.
  - Deliberately excluded from `PbsClientWin.sln`.
- `src/Pbs.ProjFs/ProjFsAvailability.cs`
  - ProjFS driver probe plus enablement guidance.
- `src/Pbs.ProjFs/PxarMountSession.cs`
  - Read-only ProjFS provider backed by catalog enumeration and hydrated pxar temp-stream reads.

## Files Changed

- `src/Pbs.Cli/Pbs.Cli.csproj`
  - Adds `Pbs.ProjFs` as a conditional project reference for `-windows` target frameworks only.
- `src/Pbs.Cli/Program.cs`
  - Adds `mount` command dispatch and usage line.
  - Adds non-Windows clear unsupported message.
  - Adds Windows-only M1 mount body:
    - checks ProjFS availability,
    - opens `ReaderSession`,
    - restores `catalog.pcat1.didx`,
    - hydrates the requested pxar archive to a temp file,
    - builds the `PxarIndexer` lookup,
    - starts `PxarMountSession`,
    - waits for Ctrl-C and deletes the temp file on exit.

## Validation

- `dotnet build src/Pbs.Pxar/Pbs.Pxar.csproj`
  - Passed: 0 warnings, 0 errors.
- `dotnet test tests/Pbs.Pxar.Tests/Pbs.Pxar.Tests.csproj`
  - Passed: 27 total, 0 failed, 0 skipped.
- `dotnet build PbsClientWin.sln`
  - Passed: 0 warnings, 0 errors.
- Existing requested suites:
  - `tests/Pbs.Protocol.Tests`: 54 passed, 0 failed.
  - `tests/Pbs.Nbd.Tests`: 9 passed, 0 failed.
  - `tests/Pbs.Cli.Tests`: 8 passed, 0 failed.

## Windows Build Status

NOT YET COMPILED ON WINDOWS: `src/Pbs.ProjFs` + mount `#if WINDOWS` body — pending VM144 build.

## ProjFS API Notes

- Authored against `Microsoft.Windows.ProjFS` 2.1.0 and the managed API sample callback shape.
- M1 symlink entries are indexed and exposed as read-only reparse-point metadata in placeholders, but symlink target projection may need adjustment during VM144 validation because ProjFS symlink placeholder support depends on the managed wrapper's extended-info APIs and NTFS support.
- Read-only behavior is enforced through read-only placeholder attributes plus denial of delete, rename, hardlink, and convert-to-full notifications.
