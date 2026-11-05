using System.Text.Json;
using DeckCapture.Capture;
using DeckCapture.Domain;

namespace DeckCapture.Tests;

public sealed class RecordingMetricsTests
{
    [Fact]
    public async Task ActualBeginResetsOnlyRecordingDeltasAndExcludesRetainedPreRoll()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "recording-metrics-tests", Guid.NewGuid().ToString("N"));
        await using var engine = new SpoolCapture(new(new(), new() { PreRollSeconds = 2 }, directory));
        var spool = Assert.Single(Directory.GetDirectories(directory, "spool-*"));
        await File.WriteAllBytesAsync(Path.Combine(spool, "segment-000000004.mkv"), []);
        await File.WriteAllBytesAsync(Path.Combine(spool, "segment-000000005.mkv"), []);
        engine.ReadProgress("frame=125"); engine.ReadProgress("out_time_us=5000000");
        engine.ReadProgress("drop_frames=3"); engine.ReadProgress("dup_frames=2");
        Assert.Null(engine.Metrics.RecordingFrames); Assert.Null(engine.Metrics.RecordingMediaSeconds);
        engine.Begin(new("begin", "test", Path.Combine(directory, "master.mkv"), DurationSeconds: 5, PreRollSeconds: 1));
        Assert.Equal(0, engine.Metrics.RecordingFrames); Assert.Equal(0, engine.Metrics.RecordingMediaSeconds);
        Assert.Equal(125, engine.Metrics.Frames); Assert.Equal(5, engine.Metrics.MediaSeconds);
        engine.ReadProgress("frame=150"); engine.ReadProgress("out_time_us=6000000");
        var metrics = engine.Metrics;
        Assert.Equal(25, metrics.RecordingFrames); Assert.Equal(1, metrics.RecordingMediaSeconds);
        Assert.Equal(150, metrics.Frames); Assert.Equal(6, metrics.MediaSeconds);
        Assert.Equal(3, metrics.DroppedFrames); Assert.True(metrics.OutputFrameDuplication);
        Assert.Throws<InvalidOperationException>(() => engine.Begin(new("begin", "duplicate", Path.Combine(directory, "other.mkv"), DurationSeconds: 5)));
        Assert.Equal(25, engine.Metrics.RecordingFrames);
        using var timing = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(spool, "timing.json")));
        Assert.True(timing.RootElement.GetProperty("estimatedAvailablePreRollSeconds").GetDouble() >= 1);
        var roundTrip = JsonSerializer.Deserialize<WorkerEvent>(JsonSerializer.Serialize(new WorkerEvent("metrics", Metrics: metrics)))!.Metrics!;
        Assert.Equal(metrics.RecordingFrames, roundTrip.RecordingFrames);
        Assert.Equal(metrics.RecordingMediaSeconds, roundTrip.RecordingMediaSeconds);
    }

    [Theory]
    [InlineData("progressive", "60000/1001", "1920×1080 progressive @ 59.94 fps")]
    [InlineData("tt", "25/1", "1920×1080 interlaced @ 25 fps")]
    [InlineData("bt", "30000/1001", "1920×1080 interlaced @ 29.97 fps")]
    [InlineData("unknown", "0/0", "1920×1080 field order unknown (rate unknown)")]
    public void DetectedFormatReportsRealStreamMetadataWithoutInventingFieldRate(string fieldOrder, string rate, string expected)
    {
        var json = JsonSerializer.Serialize(new { streams = new object[]
        {
            new { codec_type = "video", width = 1920, height = 1080, field_order = fieldOrder, avg_frame_rate = rate },
            new { codec_type = "audio", channels = 8 }
        }});
        var metadata = SegmentMonitor.ParseStreamMetadata(json);
        Assert.Equal(8, metadata.AudioChannels); Assert.Equal(expected, metadata.VideoFormat);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":\"1920\",\"height\":1080}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\"}]}")]
    public void MissingOrUnsupportedMetadataStaysUnknown(string json)
    {
        Assert.Null(SegmentMonitor.ParseStreamMetadata(json).VideoFormat);
        Assert.Null(new CaptureMetrics().DetectedVideoFormat);
    }
}
