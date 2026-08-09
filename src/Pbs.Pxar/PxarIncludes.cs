using System.Text;
using System.Text.RegularExpressions;

namespace Pbs.Pxar;

/// <summary>
/// Matches directory entries against glob include patterns during pxar extraction. A pattern without
/// a slash matches an entry's <em>name</em> anywhere in the tree; a pattern with a slash matches
/// the whole relative path (using forward-slash separators). Supports <c>*</c> (within a path
/// segment), <c>**</c> (across segments) and <c>?</c>. An empty set matches everything.
/// </summary>
public sealed class PxarIncludes
{
    private readonly List<(Regex Regex, bool NameOnly)> _patterns = new();

    public PxarIncludes(IEnumerable<string>? patterns)
    {
        if (patterns is null) return;
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

    /// <summary>True if the entry (relative path + name, using '/' separators) matches any pattern (or none are set).</summary>
    public bool IsMatch(string relativePath, string name)
    {
        if (IsEmpty) return true;
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
