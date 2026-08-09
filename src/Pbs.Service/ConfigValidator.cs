using Cronos;

namespace Pbs.Service;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(ServiceConfig config)
    {
        var errors = new List<string>();

        if (config.Jobs.Count == 0)
        {
            errors.Add("jobs must contain at least one job.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in config.Jobs)
        {
            var label = string.IsNullOrWhiteSpace(job.Id) ? "<missing id>" : job.Id;

            if (string.IsNullOrWhiteSpace(job.Id))
            {
                errors.Add("job id is required.");
            }
            else if (!ids.Add(job.Id))
            {
                errors.Add($"job '{job.Id}' has a duplicate id.");
            }

            if (string.IsNullOrWhiteSpace(job.Repository))
            {
                errors.Add($"job '{label}' repository is required.");
            }

            var hasSources = job.Sources.Count > 0;
            var hasImageSource = !string.IsNullOrWhiteSpace(job.ImageSource);
            if (hasSources == hasImageSource)
            {
                errors.Add($"job '{label}' must set either sources or imageSource, but not both.");
            }

            foreach (var source in job.Sources)
            {
                if (!LooksLikeArchiveSpec(source))
                {
                    errors.Add($"job '{label}' source '{source}' must look like <name.pxar>:<path>.");
                }
            }

            if (hasImageSource && !LooksLikeArchiveSpec(job.ImageSource!))
            {
                errors.Add($"job '{label}' imageSource '{job.ImageSource}' must look like <name.img>:<volume>.");
            }

            if (string.IsNullOrWhiteSpace(job.Schedule))
            {
                errors.Add($"job '{label}' schedule is required.");
            }
            else
            {
                try
                {
                    CronExpression.Parse(job.Schedule, CronFormat.Standard);
                }
                catch (CronFormatException ex)
                {
                    errors.Add($"job '{label}' schedule is invalid: {ex.Message}");
                }
            }

            if (job.Retention is not null && !HasAnyRetention(job.Retention))
            {
                errors.Add($"job '{label}' retention must set at least one keep value.");
            }

            ValidateNonNegativeRetention(errors, label, job.Retention);
        }

        return errors;
    }

    private static bool LooksLikeArchiveSpec(string source)
    {
        var separator = source.IndexOf(':');
        return separator > 0 && separator + 1 < source.Length;
    }

    private static bool HasAnyRetention(RetentionConfig retention) =>
        retention.KeepLast.HasValue ||
        retention.KeepDaily.HasValue ||
        retention.KeepWeekly.HasValue ||
        retention.KeepMonthly.HasValue;

    private static void ValidateNonNegativeRetention(List<string> errors, string label, RetentionConfig? retention)
    {
        if (retention is null)
        {
            return;
        }

        AddIfInvalid(errors, label, "keepLast", retention.KeepLast);
        AddIfInvalid(errors, label, "keepDaily", retention.KeepDaily);
        AddIfInvalid(errors, label, "keepWeekly", retention.KeepWeekly);
        AddIfInvalid(errors, label, "keepMonthly", retention.KeepMonthly);
    }

    private static void AddIfInvalid(List<string> errors, string label, string name, int? value)
    {
        if (value <= 0)
        {
            errors.Add($"job '{label}' retention {name} must be greater than zero.");
        }
    }
}
