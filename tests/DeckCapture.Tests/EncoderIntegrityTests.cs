using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed class EncoderIntegrityTests
{
    [Fact]
    public void ActualHardwareOverrunAndDuplicationWarningsRemainStickyAcrossRepeatedSummaries()
    {
        var monitor = new EncoderIntegrityMonitor();
        monitor.Observe("Last message repeated 3873 times");
        Assert.False(monitor.InputBufferOverrun); Assert.False(monitor.OutputFrameDuplication); Assert.Null(monitor.Warning);
        monitor.Observe("[in#0/decklink @ 0000020d2d57e6c0] Decklink input buffer overrun!");
        Assert.True(monitor.InputBufferOverrun); Assert.False(monitor.OutputFrameDuplication);
        Assert.Contains("exact lost-frame count is unavailable", monitor.Warning);
        monitor.Observe("Last message repeated 3873 times");
        monitor.Observe("[vf#0:0 @ 0000020d2f5d0940] More than 1000 frames duplicated");
        monitor.Observe("[in#0/decklink @ 0000020d2d57e6c0] Frame received (#500) - Input returned - Frames dropped 2");
        monitor.ObserveDuplicatedFrames(0);
        Assert.True(monitor.InputBufferOverrun); Assert.True(monitor.OutputFrameDuplication);
        Assert.Contains("exact loss counts are unavailable", monitor.Warning);
        Assert.DoesNotContain("3873", monitor.Warning); // Repeated lines are not invented counters.
    }

    [Fact]
    public void LegacyWarningAndPositiveProgressDuplicationAreAcceptedButOtherInputsCannotFakeOverruns()
    {
        var monitor = new EncoderIntegrityMonitor();
        monitor.Observe("[in#0/lavfi @ 1] Decklink input buffer overrun!");
        monitor.Observe("[out#0/decklink @ 1] Decklink input buffer overrun!");
        monitor.Observe("Decklink input buffer overrun!");
        Assert.False(monitor.InputBufferOverrun);
        monitor.Observe("[decklink @ 000001] Decklink input buffer overrun!"); Assert.True(monitor.InputBufferOverrun);
        monitor.ObserveDuplicatedFrames(0); Assert.False(monitor.OutputFrameDuplication);
        monitor.ObserveDuplicatedFrames(1); Assert.True(monitor.OutputFrameDuplication);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(64)]
    public void PreservationUsesConfiguredThreadsAndTimestampPassthroughWithoutChangingFfv1Pcm(int threads)
    {
        var args = SpoolCapture.BuildEncoderArguments(new(new() { SourceKind = "DeckLink", DeviceName = "Exact source", FormatCode = "auto" },
            new() { EncoderThreads = threads }, "unused"), "unused").ToList();
        Assert.Equal(threads.ToString(System.Globalization.CultureInfo.InvariantCulture), args[args.IndexOf("-threads:v") + 1]);
        Assert.Equal("passthrough", args[args.IndexOf("-fps_mode:v") + 1]);
        Assert.True(args.IndexOf("-fps_mode:v") > args.IndexOf("-i"));
        Assert.Equal("ffv1", args[args.IndexOf("-c:v") + 1]); Assert.Equal("3", args[args.IndexOf("-level") + 1]);
        Assert.Equal("pcm_s24le", args[args.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-r", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(-1)]
    public void InvalidThreadBudgetsFailBothConfigurationAndWorkerAdmission(int threads)
    {
        var profile = new CaptureProfile { EncoderThreads = threads };
        Assert.Throws<ArgumentException>(() => SpoolCapture.ValidateOptions(new(new(), profile, "unused")));
        var config = ConfigurationRules.DevelopmentDefaults(Path.GetTempPath()) with { Capture = profile };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("encoder threads", StringComparison.Ordinal));
        Assert.Equal(2, new CaptureProfile().EncoderThreads);
    }
}
