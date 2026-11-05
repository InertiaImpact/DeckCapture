using System.Buffers.Binary;
using DeckCapture.Capture;
using DeckCapture.Domain;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class AudioChannelMeterTests
{
    [Fact]
    public void UnequalStereoTonesRetainIndependentLevels()
    {
        var pcm = TonePcm(2, channel => channel == 0 ? .5 : .05);
        var levels = SegmentMonitor.AnalyzeChannelPcm(pcm, 2);
        Assert.Equal(2, levels.Length);
        Assert.InRange(levels[0], -9.04, -9.02);
        Assert.InRange(levels[1], -29.05, -29.02);
        Assert.InRange(levels[0] - levels[1], 19.99, 20.03);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    public void SilentFirstChannelIsNotContaminatedByOtherInterleavedChannels(int channels)
    {
        var pcm = TonePcm(channels, channel => channel == 0 ? 0 : channel == 1 ? .25 : .9);
        var levels = SegmentMonitor.AnalyzeChannelPcm(pcm, channels);
        Assert.Equal(2, levels.Length);
        Assert.Equal(-100, levels[0]);
        Assert.InRange(levels[1], -15.07, -15.04);
        Assert.All(levels, level => Assert.True(double.IsFinite(level)));
    }

    [Fact]
    public void MonoHasOneRealChannelAndNoInventedSecondMeter()
    {
        var levels = SegmentMonitor.AnalyzeChannelPcm(TonePcm(1, _ => .5), 1);
        Assert.Single(levels); Assert.InRange(levels[0], -9.04, -9.02);
    }

    [Fact]
    public void MissingAndIncompleteAudioStaysUnavailable()
    {
        Assert.Empty(new CaptureMetrics().AudioChannelRmsDbfs);
        Assert.Empty(SegmentMonitor.AnalyzeChannelPcm([], 2));
        Assert.Empty(SegmentMonitor.AnalyzeChannelPcm([0, 0], 0));
        Assert.Empty(SegmentMonitor.AnalyzeChannelPcm([0, 0, 1], 2));
        Assert.Empty(SegmentMonitor.AnalyzeChannelPcm([0, 0], 17));
    }

    [Fact]
    public async Task OptionalChannelSampleReadFailuresReturnUnavailableLevels()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "audio-meter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Assert.Empty(await SegmentMonitor.ReadChannelSampleAsync(Path.Combine(directory, "missing.pcm"), 2, CancellationToken.None));
        Assert.Empty(await SegmentMonitor.ReadChannelSampleAsync(directory, 2, CancellationToken.None));
        var path = Path.Combine(directory, "sample.pcm");
        await File.WriteAllBytesAsync(path, TonePcm(2, channel => channel == 0 ? .5 : .05));
        Assert.Equal(2, (await SegmentMonitor.ReadChannelSampleAsync(path, 2, CancellationToken.None)).Length);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Empty(await SegmentMonitor.ReadChannelSampleAsync(path, 2, canceled.Token));
        await File.WriteAllBytesAsync(path, new byte[512 * 1024 + 1]);
        Assert.Empty(await SegmentMonitor.ReadChannelSampleAsync(path, 2, CancellationToken.None));
    }

    [CaptureIntegrationFact]
    public Task RealStereoSegmentMetersItsOriginalChannelsAndPreservesMonoDetection() => CheckSegmentAsync(stereo: true);

    [CaptureIntegrationFact]
    public Task RealMonoSegmentDoesNotProduceASecondMeter() => CheckSegmentAsync(stereo: false);

    [CaptureIntegrationFact]
    public Task MissingHeaderProbeLeavesMetersUnavailableWithoutChangingMonoAnalysis() => CheckSegmentAsync(stereo: true, missingProbe: true);

    private static async Task CheckSegmentAsync(bool stereo, bool missingProbe = false)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "audio-meter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var segment = Path.Combine(directory, "sample.mkv");
        var source = "sine=frequency=1000:sample_rate=48000" + (stereo ? ",pan=stereo|c0=c0|c1=0.1*c0" : "");
        var profile = new CaptureProfile();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=black:s=64x36:r=25",
            "-f", "lavfi", "-i", source, "-map", "0:v:0", "-map", "1:a:0", "-t", "1", "-c:v", "ffv1", "-threads:v", "1", "-c:a", "pcm_s24le", segment], timeout.Token);
        var analyzer = new SegmentMonitor();
        if (missingProbe) profile = profile with { FfprobePath = Path.Combine(directory, "missing-ffprobe.exe") };
        var metrics = await analyzer.AnalyzeAsync(profile, segment, directory, synthetic: true);
        Assert.True(metrics.AnalysisAvailable);
        // Header discovery is optional and has a one-second production budget.
        // A busy host may need a later segment; scalar monitoring must stay valid
        // throughout, and the same owned analyzer must eventually learn metadata.
        for (var nextSegment = 0; !missingProbe && metrics.DetectedVideoFormat is null && nextSegment < 3; nextSegment++)
        {
            Assert.Empty(metrics.AudioChannelRmsDbfs);
            Assert.Equal(64 * 36, new FileInfo(Path.Combine(directory, "monitor.gray")).Length);
            var mono = await File.ReadAllBytesAsync(Path.Combine(directory, "monitor.pcm"));
            Assert.Equal(SegmentMonitor.AnalyzeSamples(await File.ReadAllBytesAsync(Path.Combine(directory, "monitor.gray")), mono).AudioRmsDbfs, metrics.AudioRmsDbfs);
            metrics = await analyzer.AnalyzeAsync(profile, segment, directory, synthetic: true);
            Assert.True(metrics.AnalysisAvailable);
        }
        if (!missingProbe) Assert.StartsWith("64×36", metrics.DetectedVideoFormat);
        else Assert.Null(metrics.DetectedVideoFormat);
        Assert.Equal(missingProbe ? 0 : stereo ? 2 : 1, metrics.AudioChannelRmsDbfs.Length);
        if (stereo && !missingProbe) Assert.InRange(metrics.AudioChannelRmsDbfs[0] - metrics.AudioChannelRmsDbfs[1], 19.8, 20.2);
        var existing = SegmentMonitor.AnalyzeSamples(await File.ReadAllBytesAsync(Path.Combine(directory, "monitor.gray")), await File.ReadAllBytesAsync(Path.Combine(directory, "monitor.pcm")));
        Assert.Equal(existing.AudioRmsDbfs, metrics.AudioRmsDbfs);
        Assert.InRange(new FileInfo(Path.Combine(directory, "monitor-channels.pcm")).Length, 2, 48000 * (stereo ? 2 : 1) * 2 / 4);
        if (!missingProbe)
        {
            // Channel metadata is cached within this owned analyzer, not reprobed every segment.
            var again = await analyzer.AnalyzeAsync(profile with { FfprobePath = Path.Combine(directory, "missing-ffprobe.exe") }, segment, directory, synthetic: true);
            Assert.Equal(metrics.AudioChannelRmsDbfs, again.AudioChannelRmsDbfs);
        }
    }

    private static byte[] TonePcm(int channels, Func<int, double> amplitude)
    {
        var pcm = new byte[12000 * channels * 2];
        for (var frame = 0; frame < 12000; frame++)
            for (var channel = 0; channel < channels; channel++)
            {
                var sample = (short)Math.Round(32767 * amplitude(channel) * Math.Sin(2 * Math.PI * 1000 * frame / 48000));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((frame * channels + channel) * 2, 2), sample);
            }
        return pcm;
    }
}
