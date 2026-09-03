using Lsc.Inventory.Api.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record IaaIScheduleDecision(bool ShouldRun, string Reason, DateTimeOffset UtcNow, DateTime LocalNow);

public static class IaaIScheduleWindow
{
    public static IaaIScheduleDecision Evaluate(DateTimeOffset utcNow, IaaINationalOptions options)
    {
        var timeZone = ResolveTimeZone(options.ScheduleTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        var isMidnightEnd = options.ScheduleEndLocalHour == 24;
        var withinWindow = isMidnightEnd
            ? localNow.Hour >= options.ScheduleStartLocalHour || localNow.Hour == 0
            : localNow.Hour >= options.ScheduleStartLocalHour && localNow.Hour <= options.ScheduleEndLocalHour;
        if (!withinWindow)
            return new(false, "outside-operating-window", utcNow, localNow);
        var effectiveLocalHour = isMidnightEnd && localNow.Hour == 0 ? 24 : localNow.Hour;
        var elapsedHours = effectiveLocalHour - options.ScheduleStartLocalHour;
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
