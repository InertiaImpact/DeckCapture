using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using System.Diagnostics;

namespace DeckCapture.Tests;

public sealed class SignalObservationTests
{
    [Fact]
    public void HardwareLockDefaultsUnknownAndPacketFlowDoesNotEstablishIt()
    {
        var metrics = new CaptureMetrics { Frames = 250, MediaSeconds = 10, IsBlack = true, BlackRatio = 1 };
        Assert.Null(metrics.SignalLocked); Assert.Null(metrics.InputSignalPresent);
        Assert.False(metrics.AnalysisAvailable); Assert.Null(metrics.AnalysisAgeSeconds);
        Assert.False(SignalObservationPolicy.IsContent(metrics));
        Assert.Null(SignalObservationPolicy.LossWarning(metrics));
    }

    [Fact]
    public void AnalysisAgeContinuesIncreasingWhenWorkerProtocolStopsUpdating()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var observation = new CaptureMetricsObservation(new() { AnalysisAvailable = true, AnalysisAgeSeconds = 1 }, timestamp);
        var aged = observation.Current(timestamp + 6 * Stopwatch.Frequency);
        Assert.InRange(aged.AnalysisAgeSeconds!.Value, 6.99, 7.01);
        Assert.False(SignalObservationPolicy.CanAnalyzeContent(aged));
        Assert.Null(new CaptureMetricsObservation(new(), timestamp).Current(timestamp + Stopwatch.Frequency).AnalysisAgeSeconds);
    }

    [Fact]
    public void OnlyOwnedDeckLinkWarningTransitionsEstablishReportedPresence()
    {
        var monitor = new DeckLinkSignalMonitor();
        Assert.Null(monitor.InputSignalPresent);
        foreach (var line in new[] { "frame=250", "drop_frames=0", "[decklink @ 0x123] Frame received (#25) - Valid (100B) - QSize 1MB",
            "Input returned", "[other @ 1] Frame received (#1) - Input returned - Frames dropped 1" }) monitor.Observe(line);
        Assert.Null(monitor.InputSignalPresent);
        monitor.Observe("[decklink @ 000001A] Frame received (#1) - No input signal detected - Frames dropped 1");
        Assert.False(monitor.InputSignalPresent);
        monitor.Observe("frame=500"); Assert.False(monitor.InputSignalPresent);
        monitor.Observe("[decklink @ 000001A] Frame received (#500) - Input returned - Frames dropped 2");
        Assert.True(monitor.InputSignalPresent);
        monitor.Observe("[decklink @ 000001A] Frame received (#750) - No input signal detected - Frames dropped 3");
        Assert.False(monitor.InputSignalPresent);
    }

    [Fact]
    public void CurrentFfmpegInputPrefixParsesActualHardwareLossAndRecoveryOnlyForDeckLink()
    {
        var monitor = new DeckLinkSignalMonitor();
        monitor.Observe("[in#0/decklink @ 0000026adc78e2c0] Frame received (#1) - No input signal detected - Frames dropped 1");
        Assert.False(monitor.InputSignalPresent);
        monitor.Observe("[in#0/decklink @ 0000026adc78e2c0] Frame received (#500) - Input returned - Frames dropped 2");
        Assert.True(monitor.InputSignalPresent);
        foreach (var prefix in new[] { "in#0/dshow", "in#2/lavfi", "out#0/decklink", "in#x/decklink", "in#0/decklink-other" })
            monitor.Observe($"[{prefix} @ 0000026adc78e2c0] Frame received (#501) - No input signal detected - Frames dropped 3");
        Assert.True(monitor.InputSignalPresent);
        monitor.Observe("[in#12/decklink @ 0000026adc78e2c0] Frame received (#502) - No input signal detected - Frames dropped 4");
        Assert.False(monitor.InputSignalPresent);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, true, true)]
    [InlineData(true, null, true)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    public void FreshAnalyzedPictureAllowsUnknownLockButExplicitNegativeBlocks(bool? locked, bool? present, bool expected)
    {
        var metrics = new CaptureMetrics { SignalLocked = locked, InputSignalPresent = present, AnalysisAvailable = true,
            AnalysisAgeSeconds = 1, IsBlack = false, BlackRatio = .1, AudioRmsDbfs = -20 };
        Assert.Equal(expected, SignalObservationPolicy.IsContent(metrics));
        Assert.Equal(!expected, SignalObservationPolicy.LossWarning(metrics) is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(5.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void StaleOrMissingAnalysisDoesNotTriggerContentOrTapeEnd(double? age)
    {
        var metrics = new CaptureMetrics { SignalLocked = true, AnalysisAvailable = true, AnalysisAgeSeconds = age,
            IsBlack = true, BlackRatio = 1, AudioRmsDbfs = -100 };
        Assert.False(SignalObservationPolicy.HasFreshAnalysis(metrics)); Assert.False(SignalObservationPolicy.CanAnalyzeContent(metrics));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData("Hi50", true)]
    [InlineData("pal ", true)]
    [InlineData("", false)]
    [InlineData("-i", false)]
    [InlineData("a\nb", false)]
    [InlineData("a-1", false)]
    [InlineData("averylongformat", false)]
    public void ConfigurationValidatesAutoAndPreservesExistingManualFormats(string code, bool expected)
    {
        var config = ConfigurationRules.DevelopmentDefaults(Path.GetTempPath()) with
        { Channels = [new() { Id = "test", SourceKind = "DeckLink", DeviceName = "exact", FormatCode = code }] };
        Assert.Equal(expected, ConfigurationRules.Validate(config).Count == 0);
        Assert.Equal(code, config.Channels[0].FormatCode);
    }
}
