using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Xunit;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class QcTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(8, 2, 1)]
    [InlineData(16, 4, 2)]
    [InlineData(40, 8, 4)]
    [InlineData(128, 8, 4)]
    public void FullQcUsesIndependentBoundedDecoderAndFilterBudgets(int processors, int decoderThreads, int filterThreads)
    {
        const string path = "C:\\retained media\\QC ' source.mkv";
        var arguments = OfflineQcService.BuildAnalysisArguments(path, new(), ["signalstats", "metadata=mode=print"], true, processors).ToList();
        Assert.Equal(decoderThreads.ToString(), arguments[arguments.IndexOf("-threads:v") + 1]);
        Assert.Equal("slice", arguments[arguments.IndexOf("-thread_type:v") + 1]);
        Assert.Equal(filterThreads.ToString(), arguments[arguments.IndexOf("-filter_threads") + 1]);
        Assert.Equal("1", arguments[arguments.IndexOf("-filter_complex_threads") + 1]);
        Assert.True(arguments.IndexOf("-threads:v") < arguments.IndexOf("-i"));
        Assert.Equal(path, arguments[arguments.IndexOf("-i") + 1]); Assert.Single(arguments, argument => argument == path);
        Assert.Contains("-xerror", arguments); Assert.Contains("explode", arguments);
        Assert.DoesNotContain("-read_intervals", arguments); Assert.DoesNotContain("-t", arguments); Assert.DoesNotContain("-skip_frame", arguments);
    }

    [Fact]
    public void ProgressIsThrottledButPhaseTransitionsAndFinalActualCountsAreImmediate()
    {
        var clock = new QcClock(); var updates = new List<OfflineQcProgress>();
        var reporter = new QcProgressReporter(updates.Add, clock);
        reporter.Report("Queued", 0, 0, null);
        reporter.Report("Preflight", 0, 0, null);
        reporter.Report("Analyzing", 0, 0, 10);
        for (var frame = 1; frame < 100; frame++) reporter.Report("Analyzing", frame, frame / 25d, 10);
        Assert.Equal(3, updates.Count);
        clock.Advance(); reporter.Report("Analyzing", 100, 4, 10, 1.25);
        reporter.Report("Hashing", 250, 10, 10);
        reporter.Report("Complete", 250, 10, 10);
        Assert.Equal(6, updates.Count); Assert.Equal(1.25, updates[3].Speed);
        Assert.Equal(250, updates[^1].Frames); Assert.Equal(10, updates[^1].ProcessedSeconds);
        Assert.Null(updates[0].DurationSeconds);
        new QcProgressReporter(_ => throw new InvalidOperationException("Unreliable telemetry consumer")).Report("Complete", 1, 1, 1);
    }

    private sealed class QcClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance() => ticks += TimeSpan.TicksPerSecond;
    }
    [Theory]
    [InlineData(" .S signalstats V->V Generate statistics", "signalstats", true)]
    [InlineData(" .S. signalstats V->V Generate statistics", "signalstats", true)]
    [InlineData(" T.C metadata V->V Frame metadata", "metadata", true)]
    [InlineData(" .. silencedetect A->A Detect silence\r\n", "silencedetect", true)]
    [InlineData(" T. metadata V->V Frame metadata", "metadata", true)]
    [InlineData(" .. signalstats_extra V->V Other filter", "signalstats", false)]
    [InlineData(" T.. = signalstats is described in this header", "signalstats", false)]
    [InlineData(" .. signalstats is unavailable", "signalstats", false)]
    public void FilterAvailabilityAcceptsOldAndNewTablesWithoutMatchingProse(string listing, string name, bool expected)
        => Assert.Equal(expected, OfflineQcService.HasFilter(listing, name));

    [Fact]
    public void ParserAggregatesTenBitValuesAndCapsThresholdIntervals()
    {
        var parser = new QcLogParser(new() { MaximumIntervals = 2, LowLumaThreshold = .1, HighLumaThreshold = .9, HighSaturationThreshold = .5 }, 10, .04);
        FeedFrame(parser, 0, 0, 0, 1023, 724);
        FeedFrame(parser, 1, .04, 512, 512, 0);
        parser.Complete(.08);
        Assert.Equal(2, parser.FrameCount);
        Assert.Equal(2, parser.Measures["YMIN"].Samples);
        Assert.Equal(0, parser.Measures["YMIN"].Minimum);
        Assert.Equal(512, parser.Measures["YMIN"].Maximum);
        Assert.Equal(256, parser.Measures["YMIN"].Mean);
        Assert.Equal(2, parser.Intervals.Count);
        Assert.Equal(1, parser.OmittedIntervals);
        Assert.All(parser.Intervals, interval => { Assert.Equal(0, interval.StartSeconds); Assert.Equal(.04, interval.EndSeconds); });
        Assert.Equal(1, parser.Intervals.Single(interval => interval.Kind == "highLuma").PeakValue);
    }

    [Fact]
    public void ParserKeepsPerChannelSilenceAndMarksUnclosedFreezeIntervals()
    {
        var parser = new QcLogParser(new(), 10, .04);
        parser.Accept("[silencedetect] channel: 0 | silence_start: 0.1");
        parser.Accept("[silencedetect] channel: 1 | silence_start: 0.2");
        parser.Accept("[silencedetect] channel: 0 | silence_end: 0.6 | silence_duration: 0.5");
        parser.Accept("[freezedetect] lavfi.freezedetect.freeze_start: 0.3");
        parser.Accept("[blackdetect] black_start:0 black_end:0.5 black_duration:0.5");
        parser.Complete(1);
        Assert.Equal(4, parser.Intervals.Count);
        Assert.Equal(.6, parser.Intervals.Single(interval => interval.Kind == "silence" && interval.AudioChannel == 0).EndSeconds);
        Assert.True(parser.Intervals.Single(interval => interval.Kind == "silence" && interval.AudioChannel == 1).OpenAtEnd);
        Assert.Equal(1, parser.Intervals.Single(interval => interval.Kind == "freeze").EndSeconds);
        Assert.True(parser.Intervals.Single(interval => interval.Kind == "freeze").OpenAtEnd);
        Assert.False(parser.Intervals.Single(interval => interval.Kind == "black").OpenAtEnd);
    }

    [Fact]
    public async Task SettingsAndCancellationFailBeforeStartingTools()
    {
        Assert.Throws<ArgumentException>(() => new OfflineQcOptions { MaximumIntervals = 1000000 }.Validate());
        Assert.Throws<ArgumentException>(() => new OfflineQcOptions { HighLumaThreshold = double.NaN }.Validate());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineQcService.AnalyzeAsync("does-not-exist.mkv", new(), null, canceled.Token));
    }

    [CaptureIntegrationFact]
    public async Task FullDecodeFindsBlackSilenceFreezeAndLumaWithoutChangingSource()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "qc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "black ' qc.mkv");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=black:s=96x64:r=25:d=2", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=2",
            "-t", "2", "-c:v", "ffv1", "-level", "3", "-threads:v", "1", "-pix_fmt", "yuv422p10le", "-c:a", "pcm_s24le", path }) start.ArgumentList.Add(argument);
        using var encoder = Process.Start(start)!;
        var stderr = encoder.StandardError.ReadToEndAsync(deadline.Token);
        var stdout = encoder.StandardOutput.ReadToEndAsync(deadline.Token);
        await encoder.WaitForExitAsync(deadline.Token);
        await stdout;
        Assert.True(encoder.ExitCode == 0, await stderr);
        var before = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, deadline.Token)));
        var options = new OfflineQcOptions { InputReadRate = 16, TimeoutSeconds = 30, LowLumaThreshold = .1,
            BlackMinimumSeconds = .1, FreezeMinimumSeconds = .2, SilenceMinimumSeconds = .2 };
        var progress = new List<OfflineQcProgress>();
        var report = await OfflineQcService.AnalyzeAsync(path, new CaptureProfile(), options, deadline.Token, progress.Add);
        await File.WriteAllTextAsync(Path.Combine(directory, "qc-summary.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        Assert.Equal(50, report.DecodedFrames);
        Assert.Equal(50, report.SignalStatsFrames);
        Assert.All(report.AvailableFilters.Values, Assert.True);
        Assert.Equal(before, report.SourceSha256);
        Assert.Equal(before, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, deadline.Token))));
        Assert.Contains(report.Intervals, interval => interval.Kind == "black" && interval.EndSeconds >= 1.9);
        Assert.Contains(report.Intervals, interval => interval.Kind == "freeze" && interval.OpenAtEnd && interval.EndSeconds >= 1.9);
        Assert.Contains(report.Intervals, interval => interval.Kind == "silence" && interval.AudioChannel == 0 && interval.EndSeconds >= 1.9);
        Assert.Contains(report.Intervals, interval => interval.Kind == "silence" && interval.AudioChannel == 1 && interval.EndSeconds >= 1.9);
        Assert.Contains(report.Intervals, interval => interval.Kind == "lowLuma" && interval.EndSeconds >= 1.9);
        Assert.InRange(report.Measures["YAVG"].Mean, 63, 65);
        Assert.Equal(0, report.Measures["SATMAX"].Maximum);
        Assert.Equal(new[] { "Queued", "Preflight", "Analyzing", "Hashing", "Complete" }, progress.Select(update => update.Phase).Distinct());
        Assert.Equal(50, progress[^1].Frames); Assert.Equal(2, progress[^1].ProcessedSeconds);
        Assert.Contains(progress, update => update.Phase == "Analyzing" && update.Frames == 50 && update.ProcessedSeconds > 1.9);
        Assert.All(progress.Where(update => update.Phase == "Analyzing"), update => Assert.Equal(2, update.DurationSeconds));
        // An in-flight read-rate-limited decode must terminate, release its file guard and release the concurrency gate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineQcService.AnalyzeAsync(path, new(),
            options with { TimeoutSeconds = 1, InputReadRate = .1 }, deadline.Token));
        var analyzing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = OfflineQcService.AnalyzeAsync(path, new(), options with { TimeoutSeconds = 2, InputReadRate = .1 }, deadline.Token,
            update => { if (update.Phase == "Analyzing") analyzing.TrySetResult(); });
        await analyzing.Task.WaitAsync(deadline.Token);
        var queuedProgress = new List<OfflineQcProgress>();
        using var queuedCancellation = new CancellationTokenSource();
        var queued = OfflineQcService.AnalyzeAsync(path, new(), options, queuedCancellation.Token, queuedProgress.Add);
        Assert.Equal("Queued", Assert.Single(queuedProgress).Phase);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => busy);
        var afterCancellation = await OfflineQcService.AnalyzeAsync(path, new(), options with { ComputeSha256 = false }, deadline.Token);
        Assert.Equal(50, afterCancellation.DecodedFrames);
        Assert.Null(afterCancellation.SourceSha256);
        var callbackFailure = await OfflineQcService.AnalyzeAsync(path, new(), options with { ComputeSha256 = false }, deadline.Token,
            _ => throw new InvalidOperationException("Progress consumer failed"));
        Assert.Equal(report.DecodedFrames, callbackFailure.DecodedFrames);
        Assert.Equal(report.Measures["YAVG"], callbackFailure.Measures["YAVG"]);
    }

    private static void FeedFrame(QcLogParser parser, int frame, double time, int minimum, int maximum, int saturation)
    {
        parser.Accept(FormattableString.Invariant($"[Parsed_metadata_1] frame:{frame} pts:{frame} pts_time:{time}"));
        parser.Accept($"[Parsed_metadata_1] lavfi.signalstats.YMIN={minimum}");
        parser.Accept($"[Parsed_metadata_1] lavfi.signalstats.YMAX={maximum}");
        parser.Accept($"[Parsed_metadata_1] lavfi.signalstats.SATMAX={saturation}");
        // YBITDEPTH is occupied source bits, not the pixel format's storage depth; normalization must remain ten-bit.
        parser.Accept("[Parsed_metadata_1] lavfi.signalstats.YBITDEPTH=1");
    }
}
