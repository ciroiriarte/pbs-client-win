using Pbs.Service;

namespace Pbs.Service.Tests;

public sealed class CronScheduleTests
{
    [Fact]
    public void ComputesNextThreeRuns()
    {
        var schedule = new CronSchedule("0 2 * * *");
        var now = new DateTimeOffset(2026, 1, 10, 1, 0, 0, TimeSpan.Zero);

        var runs = schedule.GetNext(now, 3);

        Assert.Equal(3, runs.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 10, 2, 0, 0, TimeSpan.Zero), runs[0]);
        Assert.Equal(new DateTimeOffset(2026, 1, 11, 2, 0, 0, TimeSpan.Zero), runs[1]);
        Assert.Equal(new DateTimeOffset(2026, 1, 12, 2, 0, 0, TimeSpan.Zero), runs[2]);
    }
}
