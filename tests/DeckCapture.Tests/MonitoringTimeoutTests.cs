using System.Text.Json;
using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Xunit;

namespace DeckCapture.Tests;

public sealed class MonitoringTimeoutTests
{
    [Fact]
    public void LegacyOptionsKeepPreparedLimitAndManualMonitoringDefaultsToUnlimited()
    {
        var options = JsonSerializer.Deserialize<CaptureOpenOptions>("{\"Channel\":{},\"Profile\":{},\"WorkingDirectory\":\"unused\"}")!;
        Assert.False(options.MonitorOnly);
        Assert.Equal(15, options.Profile.MaximumArmedMinutes);
        Assert.Equal(0, options.Profile.MonitoringTimeoutMinutes);
        Assert.False(SpoolCapture.IsMonitoringExpired(options, false, TimeSpan.FromMinutes(14.999)));
        Assert.True(SpoolCapture.IsMonitoringExpired(options, false, TimeSpan.FromMinutes(15)));
        Assert.False(SpoolCapture.IsMonitoringExpired(options with { MonitorOnly = true }, false, TimeSpan.FromDays(365)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(1440)]
    public void ManualTimeoutUsesItsOwnMonotonicElapsedBoundary(int minutes)
    {
        var options = new CaptureOpenOptions(new(), new() { MonitoringTimeoutMinutes = minutes }, "unused") { MonitorOnly = true };
        Assert.False(SpoolCapture.IsMonitoringExpired(options, false, TimeSpan.FromMinutes(minutes) - TimeSpan.FromTicks(1)));
        Assert.True(SpoolCapture.IsMonitoringExpired(options, false, TimeSpan.FromMinutes(minutes)));
        Assert.Contains($"{minutes}-minute timeout", SpoolCapture.MonitoringExpiryMessage(options));
        Assert.DoesNotContain("armed", SpoolCapture.MonitoringExpiryMessage(options));
        Assert.Contains("15 armed minutes", SpoolCapture.MonitoringExpiryMessage(options with { MonitorOnly = false }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeitherTimeoutCanExpireAnActualRecording(bool monitorOnly)
    {
        var options = new CaptureOpenOptions(new(), new() { MaximumArmedMinutes = 15, MonitoringTimeoutMinutes = 1 }, "unused") { MonitorOnly = monitorOnly };
        Assert.False(SpoolCapture.IsMonitoringExpired(options, true, TimeSpan.FromDays(2)));
        // A recording-capable session must not inherit the much shorter manual-monitor limit.
        Assert.False(SpoolCapture.IsMonitoringExpired(options with { MonitorOnly = false }, false, TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void InvalidTimeoutFailsBothSupervisorAndWorkerAdmission(int minutes)
    {
        var profile = new CaptureProfile { MonitoringTimeoutMinutes = minutes };
        Assert.Throws<ArgumentException>(() => SpoolCapture.ValidateOptions(new(new(), profile, "unused")));
        var config = ConfigurationRules.DevelopmentDefaults(Path.GetTempPath()) with { Capture = profile };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("Monitoring timeout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1440)]
    public void ValidTimeoutBoundariesAreAcceptedAndRoundTrip(int minutes)
    {
        var profile = new CaptureProfile { MonitoringTimeoutMinutes = minutes };
        SpoolCapture.ValidateOptions(new(new(), profile, "unused"));
        var config = ConfigurationRules.DevelopmentDefaults(Path.GetTempPath()) with { Capture = profile };
        Assert.DoesNotContain(ConfigurationRules.Validate(config), error => error.Contains("Monitoring timeout", StringComparison.Ordinal));
        Assert.Equal(minutes, JsonSerializer.Deserialize<CaptureProfile>(JsonSerializer.Serialize(profile))!.MonitoringTimeoutMinutes);
    }
}
