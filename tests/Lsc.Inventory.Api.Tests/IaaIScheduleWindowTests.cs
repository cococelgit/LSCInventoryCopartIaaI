using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class IaaIScheduleWindowTests
{
    private static IaaINationalOptions Options(int start = 7, int end = 23, int interval = 2) => new()
    {
        ScheduleTimeZoneId = "America/New_York",
        ScheduleStartLocalHour = start,
        ScheduleEndLocalHour = end,
        ScheduleIntervalHours = interval
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
    public void Treats_24_as_midnight_and_runs_at_the_end_of_the_cycle()
    {
        var options = Options(6, 24, 3);
        var midnight = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 3, 4, 0, 0, TimeSpan.Zero), options);
        var ninePm = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero), options);

        Assert.True(midnight.ShouldRun);
        Assert.Equal(0, midnight.LocalNow.Hour);
        Assert.True(ninePm.ShouldRun);
        Assert.Equal(21, ninePm.LocalNow.Hour);
    }

    [Fact]
    public void Rejects_hours_between_the_three_hour_slots_when_end_is_midnight()
    {
        var options = Options(6, 24, 3);
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 3, 5, 0, 0, TimeSpan.Zero), options);

        Assert.False(decision.ShouldRun);
        Assert.Equal("outside-operating-window", decision.Reason);
    }

    [Fact]
    public void Runs_when_started_inside_the_valid_hour_without_requiring_minute_zero()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 11, 30, 0, TimeSpan.Zero), Options());
        Assert.True(decision.ShouldRun);
        Assert.Equal("scheduled", decision.Reason);
    }
}
