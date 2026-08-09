using Pbs.Abstractions;

namespace Pbs.Cli;

/// <summary>Default no-op source: read files straight from the live filesystem.</summary>
public sealed class IdentitySnapshotSource : ISnapshotSource
{
    public string Map(string livePath) => livePath;
    public void Complete() { }
    public void Dispose() { }
}
