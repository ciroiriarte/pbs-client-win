namespace Pbs.Pxar;

/// <summary>A node in a tree to be encoded as pxar. Name is empty for the archive root.</summary>
public abstract class PxarNode
{
    public required string Name { get; init; }
    public long MtimeSecs { get; init; }
    public uint MtimeNanos { get; init; }
}

/// <summary>A regular file: its bytes come from <see cref="OpenRead"/> and total <see cref="Length"/>.</summary>
public sealed class PxarFileNode : PxarNode
{
    public required Func<Stream> OpenRead { get; init; }
    public required long Length { get; init; }
}

/// <summary>A directory with ordered children (the encoder sorts them by name).</summary>
public sealed class PxarDirNode : PxarNode
{
    public List<PxarNode> Children { get; } = new();
}

/// <summary>A symbolic link to <see cref="Target"/>.</summary>
public sealed class PxarSymlinkNode : PxarNode
{
    public required string Target { get; init; }
}

/// <summary>Builds a <see cref="PxarDirNode"/> tree from a real filesystem directory.</summary>
public static class PxarTree
{
    public static PxarDirNode FromDirectory(string path, PxarExcludes? excludes = null)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) throw new DirectoryNotFoundException(path);
        var root = new PxarDirNode { Name = string.Empty, MtimeSecs = ToUnix(info.LastWriteTimeUtc, out var n), MtimeNanos = n };
        Populate(root, info, info.FullName, excludes);
        return root;
    }

    private static void Populate(PxarDirNode dir, DirectoryInfo info, string rootFull, PxarExcludes? excludes)
    {
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (excludes is { IsEmpty: false } && excludes.IsExcluded(Path.GetRelativePath(rootFull, entry.FullName), entry.Name))
                continue;

            long secs = ToUnix(entry.LastWriteTimeUtc, out var nanos);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is { } target)
            {
                dir.Children.Add(new PxarSymlinkNode { Name = entry.Name, Target = target, MtimeSecs = secs, MtimeNanos = nanos });
            }
            else if (entry is DirectoryInfo di)
            {
                var child = new PxarDirNode { Name = entry.Name, MtimeSecs = secs, MtimeNanos = nanos };
                Populate(child, di, rootFull, excludes);
                dir.Children.Add(child);
            }
            else if (entry is FileInfo fi)
            {
                dir.Children.Add(new PxarFileNode
                {
                    Name = entry.Name,
                    MtimeSecs = secs,
                    MtimeNanos = nanos,
                    Length = fi.Length,
                    OpenRead = () => fi.OpenRead(),
                });
            }
        }
    }

    private static long ToUnix(DateTime utc, out uint nanos)
    {
        var dto = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        nanos = (uint)((dto.Ticks % TimeSpan.TicksPerSecond) * 100); // 100ns ticks -> ns
        return dto.ToUnixTimeSeconds();
    }
}
