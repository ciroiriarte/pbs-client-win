using Cronos;

namespace Pbs.Service;

public sealed class CronSchedule
{
    private readonly CronExpression _expression;

    public CronSchedule(string expression)
    {
        _expression = CronExpression.Parse(expression, CronFormat.Standard);
    }

    public DateTimeOffset? GetNext(DateTimeOffset afterUtc) =>
        _expression.GetNextOccurrence(afterUtc.UtcDateTime, TimeZoneInfo.Utc) is { } next
            ? new DateTimeOffset(next, TimeSpan.Zero)
            : null;

    public IReadOnlyList<DateTimeOffset> GetNext(DateTimeOffset afterUtc, int count)
    {
        var values = new List<DateTimeOffset>(count);
        var cursor = afterUtc;

        while (values.Count < count)
        {
            var next = GetNext(cursor);
            if (next is null)
            {
                break;
            }

            values.Add(next.Value);
            cursor = next.Value.AddSeconds(1);
        }

        return values;
    }
}
