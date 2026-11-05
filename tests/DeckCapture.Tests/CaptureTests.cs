using DeckCapture.Capture;
using DeckCapture.Domain;
using Xunit;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class CaptureTests
{
    [Fact]
    public void MissingFormatDefaultsToAutoWhileSavedManualFormatStillRoundTrips()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var channel = System.Text.Json.JsonSerializer.Deserialize<ChannelConfiguration>("{\"sourceKind\":\"DeckLink\",\"deviceName\":\"Exact device\"}", json)!;
        Assert.Equal("auto", channel.FormatCode);
        var args = SpoolCapture.BuildEncoderArguments(new(channel, new(), "unused"), "unused");
        Assert.DoesNotContain("-format_code", args);
        var manual = channel with { FormatCode = "Hi59" };
        Assert.Equal("Hi59", System.Text.Json.JsonSerializer.Deserialize<ChannelConfiguration>(System.Text.Json.JsonSerializer.Serialize(manual, json), json)!.FormatCode);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void AutomaticDeckLinkFormatOmitsTheOverrideOnTheSameOwnedInput(string code)
    {
        var args = SpoolCapture.BuildEncoderArguments(new(new() { SourceKind = "DeckLink", DeviceName = "Exact device", FormatCode = code }, new(), "unused"), "unused").ToList();
        Assert.DoesNotContain("-format_code", args);
        Assert.Single(args, argument => argument == "-i");
        Assert.Equal("Exact device", args[args.IndexOf("-i") + 1]);
        Assert.Equal("yuv422p10", args[args.IndexOf("-raw_format") + 1]);
        Assert.Equal("32", args[args.IndexOf("-audio_depth") + 1]);
    }

    [Theory]
    [InlineData("ntsc")]
    [InlineData("Hi50")]
    [InlineData("pal")]
    [InlineData("pal ")]
    public void ManualDeckLinkFourCcIsPreservedExactlyBeforeInput(string code)
    {
        var args = SpoolCapture.BuildEncoderArguments(new(new() { SourceKind = "DeckLink", DeviceName = "Exact device", FormatCode = code }, new(), "unused"), "unused").ToList();
        Assert.True(args.IndexOf("-format_code") < args.IndexOf("-i"));
        Assert.Equal(code, args[args.IndexOf("-format_code") + 1]);
    }

    [Fact]
    public void DeckLinkRequests32BitAudioBeforeOpeningTheInputAndKeepsPcm24Output()
    {
        var channel = new ChannelConfiguration { SourceKind = "DeckLink", DeviceName = "Exact input name", FormatCode = "ntsc", AudioChannels = 8 };
        var args = SpoolCapture.BuildEncoderArguments(new(channel, new(), "unused"), "unused").ToList();
        var input = args.IndexOf("-i");
        var depth = args.IndexOf("-audio_depth");
        Assert.True(depth >= 0 && depth < input);
        Assert.Equal("32", args[depth + 1]);
        Assert.Equal(channel.DeviceName, args[input + 1]);
        Assert.Single(args, argument => argument == "-audio_depth");
        var outputCodec = args.IndexOf("-c:a");
        Assert.True(outputCodec > input);
        Assert.Equal("pcm_s24le", args[outputCodec + 1]);
    }

    [Theory]
    [InlineData("testsrc2")]
    [InlineData("black")]
    public void SyntheticInputKeepsItsLavfiSourcesWithoutDeckLinkAudioOptions(string pattern)
    {
        var args = SpoolCapture.BuildEncoderArguments(new(new() { SyntheticPattern = pattern }, new(), "unused"), "unused").ToList();
        Assert.DoesNotContain("-audio_depth", args);
        Assert.DoesNotContain("decklink", args);
        Assert.Equal(2, args.Count(argument => argument == "-i"));
        Assert.Equal(2, args.Count(argument => argument == "-re"));
        Assert.Equal(2, args.Count(argument => argument == "lavfi"));
        Assert.Equal("pcm_s24le", args[args.IndexOf("-c:a") + 1]);
    }

    [Theory]
    [InlineData(true, true, "DeckCapture.CaptureWorker.exe")]
    [InlineData(true, false, "DeckCapture.CaptureWorker.dll")]
    [InlineData(false, true, "DeckCapture.CaptureWorker.dll")]
    public async Task BundledWorkerPrefersNativeHostOnlyWithItsWindowsRuntime(bool windows, bool bundledRuntime, string expected)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "worker-resolution", Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(directory, "capture-worker");
        Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(worker, "DeckCapture.CaptureWorker.exe"), "Resolution fixture only");
        await File.WriteAllTextAsync(Path.Combine(worker, "DeckCapture.CaptureWorker.dll"), "Resolution fixture only");
        if (bundledRuntime) await File.WriteAllTextAsync(Path.Combine(worker, "coreclr.dll"), "Resolution fixture only");
        Assert.Equal(Path.Combine(worker, expected), CaptureBackend.ResolveBundledWorkerPath(directory, windows));
    }

    [Fact]
    public async Task WorkerLogTailRetainsBoundedRedactedEvidence()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "worker-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "worker-stderr.log");
        await using (var log = new WorkerLogTail(path))
        {
            for (var index = 0; index < 250; index++) log.Append($"line-{index} password=sensitive-value C:\\private\\tape\\source.mkv");
            log.Append(new string('x', 3000));
        }
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(200, lines.Length);
        Assert.StartsWith("line-51 ", lines[0]);
        Assert.Contains("[redacted path]", lines[^2]);
        Assert.DoesNotContain("sensitive-value", string.Join('\n', lines));
        Assert.DoesNotContain("private", string.Join('\n', lines));
        Assert.All(lines, line => Assert.True(line.Length <= 2048));
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(180, 660)]
    [InlineData(3600, 7500)]
    [InlineData(7200, 14700)]
    [InlineData(int.MaxValue, 86400)]
    public void FinalizationDeadlineAccommodatesLongTapesAndRemainsBounded(int durationSeconds, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SpoolCapture.FinalizationTimeout(durationSeconds));

    [Fact]
    public void MonitoringMeasuresBlackSilenceAndRealWaveform()
    {
        var black = SegmentMonitor.AnalyzeSamples(new byte[64 * 36], new byte[4800]);
        Assert.Equal(1, black.BlackRatio);
        Assert.Equal(-100, black.AudioRmsDbfs);
        Assert.All(black.Waveform, value => Assert.Equal(0, value));
        var white = SegmentMonitor.AnalyzeSamples(Enumerable.Repeat((byte)255, 64 * 36).ToArray(), [0, 64, 0, 192]);
        Assert.Equal(0, white.BlackRatio);
        Assert.InRange(white.AudioRmsDbfs, -6.03, -6.01);
        Assert.All(white.Waveform, value => Assert.Equal(1, value));
    }

    [Fact]
    public void RejectUnsupportedSyntheticPatternAndUnboundedSpool()
    {
        var options = new CaptureOpenOptions(new() { SyntheticPattern = "movie=untrusted" }, new(), "unused");
        Assert.Throws<ArgumentException>(() => SpoolCapture.ValidateOptions(options));
        Assert.Throws<ArgumentException>(() => SpoolCapture.ValidateOptions(options with { Channel = new(), Profile = new() { PreRollSeconds = 10000 } }));
    }

    [CaptureIntegrationFact]
    public async Task SyntheticFiveSecondRecordingHasValidMediaPreviewAndSourceMetrics()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var profile = new CaptureProfile { PreRollSeconds = 2, SyntheticWidth = 320, SyntheticHeight = 240 };
        var backend = new CaptureBackend();
        var capability = await backend.ProbeAsync(profile, deadline.Token);
        Assert.True(capability.Available, capability.Error);
        await using var session = await backend.OpenAsync(new(new() { SyntheticPattern = "testsrc2" }, profile, directory), deadline.Token);
        Assert.True(session.Metrics.SignalLocked);
        Assert.InRange(session.Metrics.BlackRatio, 0, .9);
        Assert.InRange(session.Metrics.AudioRmsDbfs, -40, -5);
        Assert.True(File.Exists(session.Metrics.PreviewPath));
        Assert.Equal(new byte[] { 255, 216 }, (await File.ReadAllBytesAsync(session.Metrics.PreviewPath!, deadline.Token))[..2]);
        var protectedOutput = Path.Combine(directory, "existing.mkv");
        await File.WriteAllTextAsync(protectedOutput, "original bytes", deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => session.BeginRecordingAsync(protectedOutput, 5, 1, deadline.Token));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(protectedOutput, deadline.Token));
        var output = Path.Combine(directory, "five second ' recording.mkv");
        await session.BeginRecordingAsync(output, 5, 1, deadline.Token);
        var result = await session.Completion.WaitAsync(deadline.Token);
        Assert.True(result.Success, result.Error);
        var media = await MediaValidation.ValidateAsync(profile.FfprobePath, output, deadline.Token);
        Assert.InRange(media.DurationSeconds, 5.8, 7.15);
        Assert.InRange(media.Frames, 145, 180);
        Assert.Equal(2, media.AudioChannels);
        Assert.Equal(0, result.DroppedFrames);
        await session.DisposeAsync();
        var workerOutput = await File.ReadAllTextAsync(Path.Combine(directory, "worker-stdout.log"), deadline.Token);
        Assert.Contains("\"Kind\":\"complete\"", workerOutput);
        Assert.DoesNotContain("preview.jpg", workerOutput);
        Assert.DoesNotContain("recording.mkv", workerOutput);
        Assert.True(File.Exists(Path.Combine(directory, "worker-stderr.log")));
    }

    [CaptureIntegrationFact]
    public async Task BlackSourceSupportsExtensionAndGracefulManualStop()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var profile = new CaptureProfile { PreRollSeconds = 1, SyntheticWidth = 320, SyntheticHeight = 240 };
        await using var session = await new CaptureBackend().OpenAsync(new(new() { SyntheticPattern = "black" }, profile, directory), deadline.Token);
        Assert.True(session.Metrics.IsBlack);
        Assert.Equal(-100, session.Metrics.AudioRmsDbfs);
        var output = Path.Combine(directory, "black.mkv");
        await session.BeginRecordingAsync(output, 3, 0, deadline.Token);
        await session.UpdateDurationAsync(10, deadline.Token);
        await Task.Delay(3500, deadline.Token);
        Assert.False(session.Completion.IsCompleted);
        var result = await session.StopAsync(deadline.Token);
        Assert.True(result.Success, result.Error);
        Assert.InRange(result.DurationSeconds, 3, 7);
        Assert.True(File.Exists(output));
    }

    [CaptureIntegrationFact]
    public async Task StopRacingAutomaticFinalizationReturnsTheAuthoritativeResultForEveryCaller()
    {
        // At these offsets the worker is entering its own timer stop, finalizing, or already done.
        // Multiple stop callers must never require an ACK from a worker already inside finalization.
        foreach (var delay in new[] { 1850, 2000, 2150, 2600 })
        {
            var directory = Path.Combine(Environment.CurrentDirectory, ".local", "capture-races", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var profile = new CaptureProfile { PreRollSeconds = 0, SyntheticWidth = 96, SyntheticHeight = 64 };
            await using var session = await new CaptureBackend().OpenAsync(new(new() { SyntheticPattern = "black" }, profile, directory), deadline.Token);
            var output = Path.Combine(directory, "race.mkv");
            await session.BeginRecordingAsync(output, 2, 0, deadline.Token);
            await Task.Delay(delay, deadline.Token);
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => session.StopAsync(deadline.Token)));
            var authoritative = await session.Completion;
            Assert.True(authoritative.Success, authoritative.Error);
            Assert.All(results, result => Assert.Equal(authoritative, result));
            Assert.True(File.Exists(output));
            Assert.Equal(authoritative, await session.StopAsync(deadline.Token));
        }
    }
}

public sealed class CaptureIntegrationFactAttribute : FactAttribute
{
    public CaptureIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DECKCAPTURE_CAPTURE_INTEGRATION") != "1")
            Skip = "Set DECKCAPTURE_CAPTURE_INTEGRATION=1 after building CaptureWorker; requires FFmpeg/ffprobe.";
    }
}
