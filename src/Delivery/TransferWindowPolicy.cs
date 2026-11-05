using System.Globalization;
using DeckCapture.Domain;

namespace DeckCapture.Delivery;

public static class TransferWindowPolicy
{
    public static bool IsWindowOpen(TransferSettings settings, string timeZone, DateTimeOffset utcNow)
    {
        if (!settings.ScheduleEnabled) return true;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var date = TimeZoneInfo.ConvertTime(utcNow, zone).Date;
        foreach (var window in settings.Windows)
        {
            var start = Parse(window.StartLocal);
            var end = Parse(window.EndLocal);
            if (start == end) throw new ArgumentException("A transfer window must have distinct start and end times.");
            // The day belongs to the start, including when the end is tomorrow.
            for (var daysBack = 0; daysBack <= 1; daysBack++)
            {
                var anchor = date.AddDays(-daysBack);
                if (!window.StartDays.Contains(anchor.DayOfWeek)) continue;
                var first = Boundary(anchor + start.ToTimeSpan(), zone, true);
                var last = Boundary(anchor.AddDays(end < start ? 1 : 0) + end.ToTimeSpan(), zone, false);
                if (utcNow >= first && utcNow < last) return true;
            }
        }
        return false;
    }

    private static TimeOnly Parse(string value) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time) ? time : throw new ArgumentException("Window times must use HH:mm.");

    private static DateTimeOffset Boundary(DateTime local, TimeZoneInfo zone, bool start)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A nonexistent wall-clock boundary moves to the first valid minute.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, start ? offsets.Max() : offsets.Min()).ToUniversalTime();
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
