using System.Text;
using System.Text.RegularExpressions;

namespace Pbs.Pxar;

/// <summary>
/// Matches directory entries against glob exclude patterns during pxar encoding. A pattern without
/// a slash matches an entry's <em>name</em> anywhere in the tree (e.g. <c>*.tmp</c>,
/// <c>node_modules</c>); a pattern with a slash matches the whole relative path (<c>/logs/**</c>).
/// Supports <c>*</c> (within a path segment), <c>**</c> (across segments) and <c>?</c>.
/// </summary>
public sealed class PxarExcludes
{
    private readonly List<(Regex Regex, bool NameOnly)> _patterns = new();

    public PxarExcludes(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            bool nameOnly = !p.Contains('/');
            var glob = nameOnly ? p : p.TrimStart('/');
            _patterns.Add((new Regex("^" + GlobToRegex(glob) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), nameOnly));
        }
    }

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>True if the entry (relative path + name, using '/' separators) is excluded.</summary>
    public bool IsExcluded(string relativePath, string name)
    {
        var rel = relativePath.Replace('\\', '/');
        foreach (var (regex, nameOnly) in _patterns)
            if (regex.IsMatch(nameOnly ? name : rel))
                return true;
        return false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') // ** = across segments
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') i++; // absorb the following slash
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
