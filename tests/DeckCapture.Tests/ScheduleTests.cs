using DeckCapture.Delivery;
using DeckCapture.Domain;

namespace DeckCapture.Tests;

public sealed class ScheduleTests
{
    private static TransferSettings Window(string start, string end, params DayOfWeek[] days) => new()
    {
        Windows = [new() { StartLocal = start, EndLocal = end, StartDays = days }]
    };

    [Theory]
    [InlineData("2026-11-02T21:59:59Z", false)]
    [InlineData("2026-11-02T22:00:00Z", true)]
    [InlineData("2026-11-03T05:59:59Z", true)]
    [InlineData("2026-11-03T06:00:00Z", false)]
    [InlineData("2026-11-03T22:00:00Z", false)]
    public void OvernightWindowUsesStartDayAndHalfOpenBoundaries(string instant, bool expected)
    {
        Assert.Equal(expected, TransferWindowPolicy.IsWindowOpen(Window("22:00", "06:00", DayOfWeek.Monday), "UTC", DateTimeOffset.Parse(instant)));
    }

    [Theory]
    [InlineData("2026-11-01T06:29:59Z", false)]
    [InlineData("2026-11-01T06:30:00Z", true)]
    [InlineData("2026-11-01T07:10:00Z", true)]
    [InlineData("2026-11-01T07:44:59Z", true)]
    [InlineData("2026-11-01T07:45:00Z", false)]
    public void FallBackUsesEarlierStartAndLaterEnd(string instant, bool expected)
    {
        Assert.Equal(expected, TransferWindowPolicy.IsWindowOpen(Window("01:30", "01:45", DayOfWeek.Sunday), "America/Chicago", DateTimeOffset.Parse(instant)));
    }

    [Theory]
    [InlineData("2026-03-08T07:59:59Z", false)]
    [InlineData("2026-03-08T08:00:00Z", true)]
    [InlineData("2026-03-08T08:29:59Z", true)]
    [InlineData("2026-03-08T08:30:00Z", false)]
    public void MissingStartAdvancesToFirstValidMinute(string instant, bool expected)
    {
        Assert.Equal(expected, TransferWindowPolicy.IsWindowOpen(Window("02:30", "03:30", DayOfWeek.Sunday), "America/Chicago", DateTimeOffset.Parse(instant)));
    }

    [Fact]
    public void MissingEndAdvancesToFirstValidMinute()
    {
        var settings = Window("01:30", "02:30", DayOfWeek.Sunday);
        Assert.True(TransferWindowPolicy.IsWindowOpen(settings, "America/Chicago", DateTimeOffset.Parse("2026-03-08T07:59:59Z")));
        Assert.False(TransferWindowPolicy.IsWindowOpen(settings, "America/Chicago", DateTimeOffset.Parse("2026-03-08T08:00:00Z")));
    }

    [Fact]
    public void OverlappingWindowsAreAUnion()
    {
        var settings = Window("10:00", "12:00", DayOfWeek.Monday) with
        {
            Windows = [new() { StartLocal = "10:00", EndLocal = "12:00", StartDays = [DayOfWeek.Monday] },
                new() { StartLocal = "11:00", EndLocal = "13:00", StartDays = [DayOfWeek.Monday] }]
        };
        Assert.True(TransferWindowPolicy.IsWindowOpen(settings, "UTC", DateTimeOffset.Parse("2026-11-02T12:30:00Z")));
        Assert.False(TransferWindowPolicy.IsWindowOpen(settings, "UTC", DateTimeOffset.Parse("2026-11-02T13:00:00Z")));
    }

    [Fact]
    public void DisabledScheduleIsOpenAndEmptyScheduleIsClosed()
    {
        Assert.True(TransferWindowPolicy.IsWindowOpen(new() { ScheduleEnabled = false, Windows = [] }, "UTC", DateTimeOffset.UtcNow));
        Assert.False(TransferWindowPolicy.IsWindowOpen(new() { Windows = [] }, "UTC", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("10:00", "10:00")]
    [InlineData("25:00", "12:00")]
    [InlineData("1:00", "12:00")]
    public void InvalidWindowsAreRejected(string start, string end)
    {
        Assert.Throws<ArgumentException>(() => TransferWindowPolicy.IsWindowOpen(Window(start, end, DayOfWeek.Monday), "UTC", DateTimeOffset.UtcNow));
    }
}
