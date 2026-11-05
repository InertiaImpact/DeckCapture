using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed partial class SupervisorTests
{
    private static CaptureMetrics DistinctMetrics(long frames) => new()
    {
        Frames = frames, MediaSeconds = 12, AudioRmsDbfs = -12, AudioChannelRmsDbfs = [-11, -32],
        AnalysisAvailable = true, AnalysisAgeSeconds = .1, SignalLocked = true, InputSignalPresent = true,
        LivePreviewAvailable = true, LivePreviewPath = "owned worker preview", PreviewPath = "owned JPEG"
    };

    private static async Task<JsonElement> ChannelSnapshotAsync(Fixture fixture) =>
        JsonSerializer.SerializeToElement(await fixture.Supervisor.SnapshotAsync(), StateStore.Json).GetProperty("channels")[0];

    private static void AssertUnavailableHandoffMetrics(JsonElement channel)
    {
        var metrics = channel.GetProperty("metrics");
        Assert.False(metrics.GetProperty("analysisAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("signalLocked").ValueKind);
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("inputSignalPresent").ValueKind);
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("analysisAgeSeconds").ValueKind);
        Assert.Equal(0, metrics.GetProperty("frames").GetInt64());
        Assert.Empty(metrics.GetProperty("audioChannelRmsDbfs").EnumerateArray());
        Assert.Equal(-100, metrics.GetProperty("audioRmsDbfs").GetDouble());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("livePreviewUrl").ValueKind);
        Assert.False(metrics.GetProperty("livePreviewAvailable").GetBoolean());
    }

    [Fact]
    public async Task MonitorToPreparedHandoffNeverExposesOldMetricsWhileDisposingOrOpening()
    {
        await using var fixture = new Fixture();
        await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var old = fixture.Backend.Sessions["ch1"]; old.Metrics = DistinctMetrics(123);
        old.DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.BlockedChannel = "ch1";
        var prepare = fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        try
        {
            await old.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var channel = await ChannelSnapshotAsync(fixture);
            var id = channel.GetProperty("job").GetProperty("id").GetString();
            Assert.Equal(id, channel.GetProperty("monitoring").GetProperty("id").GetString());
            AssertUnavailableHandoffMetrics(channel);
            old.ReleaseDisposal.TrySetResult();
            await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AssertUnavailableHandoffMetrics(await ChannelSnapshotAsync(fixture));
            fixture.Backend.ReleaseOpen.TrySetResult();
            var prepared = await prepare.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Backend.Sessions["ch1"].Metrics = DistinctMetrics(456);
            channel = await ChannelSnapshotAsync(fixture);
            Assert.Equal(456, channel.GetProperty("metrics").GetProperty("frames").GetInt64());
            Assert.Contains(prepared.Id, channel.GetProperty("metrics").GetProperty("livePreviewUrl").GetString());
        }
        finally { old.ReleaseDisposal.TrySetResult(); fixture.Backend.ReleaseOpen.TrySetResult(); }
    }

    [Fact]
    public async Task TerminalJobToOpeningMonitorHandoffDoesNotReuseRetiringRecordingMetrics()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        var job = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        var old = fixture.Backend.Sessions["ch1"]; old.Metrics = DistinctMetrics(789);
        old.DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await fixture.Supervisor.StopAsync(job.Id, job.Revision, CancellationToken.None);
            await old.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AssertUnavailableHandoffMetrics(await ChannelSnapshotAsync(fixture));
            old.ReleaseDisposal.TrySetResult();
            fixture.Backend.BlockedChannel = "ch1"; clock.Advance(60);
            Task attempt = Task.CompletedTask;
            for (var index = 0; index < 100; index++)
            {
                attempt = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
                await Task.WhenAny(attempt, fixture.Backend.OpenEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
                if (fixture.Backend.OpenEntered.Task.IsCompleted) break;
                await Task.Delay(10);
            }
            Assert.True(fixture.Backend.OpenEntered.Task.IsCompleted);
            var channel = await ChannelSnapshotAsync(fixture);
            Assert.Equal("Monitor", channel.GetProperty("monitoring").GetProperty("source").GetString());
            Assert.Equal("Opening", channel.GetProperty("monitoring").GetProperty("state").GetString());
            AssertUnavailableHandoffMetrics(channel);
            fixture.Backend.ReleaseOpen.TrySetResult(); await attempt.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { old.ReleaseDisposal.TrySetResult(); fixture.Backend.ReleaseOpen.TrySetResult(); }
    }
}
