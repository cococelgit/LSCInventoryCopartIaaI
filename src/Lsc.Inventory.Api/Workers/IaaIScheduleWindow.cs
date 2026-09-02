using Lsc.Inventory.Api.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record IaaIScheduleDecision(bool ShouldRun, string Reason, DateTimeOffset UtcNow, DateTime LocalNow);

public static class IaaIScheduleWindow
{
    public static IaaIScheduleDecision Evaluate(DateTimeOffset utcNow, IaaINationalOptions options)
    {
        var timeZone = ResolveTimeZone(options.ScheduleTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        if (localNow.Minute != 0)
            return new(false, "not-scheduled-minute", utcNow, localNow);
        if (localNow.Hour < options.ScheduleStartLocalHour || localNow.Hour > options.ScheduleEndLocalHour)
            return new(false, "outside-operating-window", utcNow, localNow);
        var elapsedHours = localNow.Hour - options.ScheduleStartLocalHour;
        if (elapsedHours % options.ScheduleIntervalHours != 0)
            return new(false, "between-scheduled-hours", utcNow, localNow);
        return new(true, "scheduled", utcNow, localNow);
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) when (id == "America/New_York")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
