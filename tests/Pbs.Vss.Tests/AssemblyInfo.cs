using Xunit;

// VSS permits only one snapshot-set creation at a time system-wide, so VSS tests must not run
// concurrently (otherwise: VSS_E_SNAPSHOT_SET_IN_PROGRESS). This mirrors the real product need for
// a single-instance guard around VSS operations (see docs/ROADMAP.md — multi-VSS-client coordination).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
