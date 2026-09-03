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
    public void Runs_at_three_hour_boundary_inside_full_window_during_edt()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), Options(6, 24, 3));
        Assert.True(decision.ShouldRun);
        Assert.Equal("scheduled", decision.Reason);
        Assert.Equal(6, decision.LocalNow.Hour);
    }

    [Fact]
    public void Runs_at_three_hour_boundary_inside_full_window_during_est()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 12, 2, 11, 0, 0, TimeSpan.Zero), Options(6, 24, 3));
        Assert.True(decision.ShouldRun);
        Assert.Equal(6, decision.LocalNow.Hour);
    }

    [Fact]
    public void Runs_inside_the_window_and_skips_only_outside_it()
    {
        var inside = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero), Options(6, 24, 3));
        var outside = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero), Options(6, 24, 3));
        Assert.True(inside.ShouldRun);
        Assert.Equal("scheduled", inside.Reason);
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
    public void Runs_at_any_minute_inside_a_valid_local_hour()
    {
        var options = Options(6, 24, 3);
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 3, 14, 37, 0, TimeSpan.Zero), options);

        Assert.True(decision.ShouldRun);
        Assert.Equal("scheduled", decision.Reason);
    }

    [Fact]
    public void Does_not_require_minute_zero_for_an_hourly_wakeup()
    {
        var decision = IaaIScheduleWindow.Evaluate(new DateTimeOffset(2026, 9, 2, 11, 30, 0, TimeSpan.Zero), Options(7, 23, 2));
        Assert.True(decision.ShouldRun);
        Assert.Equal("scheduled", decision.Reason);
    }
}
