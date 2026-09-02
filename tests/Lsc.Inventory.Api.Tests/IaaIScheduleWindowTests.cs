using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class IaaIScheduleWindowTests
{
    private static IaaINationalOptions Options() => new()
    {
        ScheduleTimeZoneId = "America/New_York",
        ScheduleStartLocalHour = 7,
        ScheduleEndLocalHour = 23,
        ScheduleIntervalHours = 2
    };

    [Fact]
    public void Runs_at_odd_local_hours_inside_full_window_during_edt()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 11, 0, 0, TimeSpan.Zero), Options());
        Assert.True(decision.ShouldRun);
        Assert.Equal("scheduled", decision.Reason);
        Assert.Equal(7, decision.LocalNow.Hour);
    }

    [Fact]
    public void Runs_at_odd_local_hours_inside_full_window_during_est()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 1, 7, 12, 0, 0, TimeSpan.Zero), Options());
        Assert.True(decision.ShouldRun);
        Assert.Equal(7, decision.LocalNow.Hour);
    }

    [Fact]
    public void Skips_between_intervals_and_outside_window()
    {
        var between = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero), Options());
        var outside = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), Options());
        Assert.False(between.ShouldRun);
        Assert.Equal("between-scheduled-hours", between.Reason);
        Assert.False(outside.ShouldRun);
        Assert.Equal("outside-operating-window", outside.Reason);
    }

    [Fact]
    public void Requires_the_scheduler_to_wake_on_the_hour()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 11, 30, 0, TimeSpan.Zero), Options());
        Assert.False(decision.ShouldRun);
        Assert.Equal("not-scheduled-minute", decision.Reason);
    }
}
