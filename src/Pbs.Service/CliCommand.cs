namespace Pbs.Service;

public sealed record CliCommand(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    public string ArgumentsForLog => string.Join(' ', Arguments.Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}
