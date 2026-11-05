using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed partial class SupervisorTests
{
    private sealed class BlockedQc
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail { get; set; }
        internal async Task<object?> Analyze(string path, CaptureProfile profile, CancellationToken token, Action<OfflineQcProgress> progress)
        {
            Assert.True(File.Exists(path));
            progress(new("Analyzing", 25, 1, 2, DateTimeOffset.UtcNow));
            Entered.TrySetResult(); await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("Controlled QC failure; retained master remains available.");
            return new { TestAnalysis = true };
        }
    }

    private static async Task<(RecordingJob Job, FakeSession Session, BlockedQc Qc)> BeginBlockedQcAsync(Fixture fixture)
    {
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with
        { Capture = config.Capture with { OfflineQcEnabled = true } }, CancellationToken.None);
        var qc = new BlockedQc(); fixture.Supervisor.OfflineQcAnalyzer = qc.Analyze;
        var job = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions[job.ChannelId];
        await fixture.Supervisor.StopAsync(job.Id, job.Revision, CancellationToken.None);
        await qc.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        return (await fixture.JobAsync(job.Id), session, qc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundQcReleasesInputAndLateCompletionCannotDisposeReplacement(bool qcFails)
    {
        await using var fixture = new Fixture();
        var (oldJob, oldSession, qc) = await BeginBlockedQcAsync(fixture);
        try
        {
            Assert.Equal(JobState.Validating, oldJob.State); Assert.True(CaptureSupervisor.IsActive(oldJob.State));
            Assert.Equal("Analyzing", oldJob.QcProgress?.Phase);
            Assert.Equal(25, oldJob.QcProgress?.Frames);
            Assert.Equal(1, oldJob.QcProgress?.ProcessedSeconds);
            Assert.Equal(oldJob.Revision, fixture.Store.ReadAll<RecordingJob>("recording").Single(j => j.Id == oldJob.Id).Revision);
            Assert.True(oldSession.Disposed); Assert.True(oldJob.ReservedBytes > 0);
            Assert.Empty(fixture.Store.ReadAll<DeliveryJob>("delivery"));
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() =>
                fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None))).StatusCode);
            var next = await fixture.Supervisor.PrepareAsync(fixture.Request(name: "Next independent master"), CancellationToken.None);
            next = await fixture.Supervisor.StartAsync(next.Id, next.Revision, CancellationToken.None);
            var replacement = fixture.Backend.Sessions["ch1"]; replacement.Metrics = DistinctMetrics(246);
            qc.Fail = qcFails; qc.Release.TrySetResult();
            var oldFinal = await fixture.WaitForFinalizedAsync(oldJob.Id);
            Assert.Equal(qcFails ? JobState.Failed : JobState.Complete, oldFinal.State);
            Assert.Equal(0, oldFinal.ReservedBytes);
            Assert.True(File.Exists(Path.Combine(oldFinal.PackageDirectory, oldFinal.FileName + ".mkv")));
            Assert.False(replacement.Disposed);
            Assert.Equal(JobState.Recording, (await fixture.JobAsync(next.Id)).State);
            var channel = await ChannelSnapshotAsync(fixture);
            Assert.Equal(next.Id, channel.GetProperty("monitoring").GetProperty("id").GetString());
            Assert.Equal(246, channel.GetProperty("metrics").GetProperty("frames").GetInt64());
        }
        finally { qc.Fail = false; qc.Release.TrySetResult(); }
    }

    [Fact]
    public async Task ValidationAllowsMonitorAndRemappingButKeepsImmutableJobAndMaintenanceProtection()
    {
        await using var fixture = new Fixture();
        await ConfigureAutomaticAsync(fixture);
        var (oldJob, oldSession, qc) = await BeginBlockedQcAsync(fixture);
        try
        {
            var config = await fixture.Supervisor.ConfigurationAsync();
            await fixture.Supervisor.UpdateConfigurationAsync(config with
            { Channels = [config.Channels[0] with { DeviceName = "new exact input" }] }, CancellationToken.None);
            Assert.Equal(oldJob.Channel.DeviceName, (await fixture.JobAsync(oldJob.Id)).Channel.DeviceName);
            await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
            var monitor = fixture.Backend.Sessions["ch1"]; Assert.NotSame(oldSession, monitor);
            monitor.Metrics = DistinctMetrics(135);
            var channel = await ChannelSnapshotAsync(fixture);
            Assert.Equal("Monitor", channel.GetProperty("monitoring").GetProperty("source").GetString());
            Assert.Equal(135, channel.GetProperty("metrics").GetProperty("frames").GetInt64());
            qc.Release.TrySetResult(); await fixture.WaitForFinalizedAsync(oldJob.Id);
            Assert.False(monitor.Disposed);
            await fixture.Supervisor.StopMonitorAsync("ch1", CancellationToken.None);
            Assert.True(monitor.Disposed);
        }
        finally { qc.Release.TrySetResult(); }
    }

    [Fact]
    public async Task InputRemainsExclusiveUntilStopSessionDisposalCompletes()
    {
        await using var fixture = new Fixture();
        var job = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await fixture.Supervisor.StopAsync(job.Id, job.Revision, CancellationToken.None);
            await session.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(JobState.Finalizing, (await fixture.JobAsync(job.Id)).State);
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(
                fixture.Request(name: "Too soon"), CancellationToken.None))).StatusCode);
            AssertUnavailableHandoffMetrics(await ChannelSnapshotAsync(fixture));
        }
        finally { session.ReleaseDisposal.TrySetResult(); }
        await fixture.WaitForFinalizedAsync(job.Id);
        Assert.Equal(JobState.Prepared, (await fixture.Supervisor.PrepareAsync(
            fixture.Request(name: "After disposal"), CancellationToken.None)).State);
    }

    [Fact]
    public async Task IdleHardwareProbeCanRunDuringQcButShutdownStillDrainsValidation()
    {
        await using var fixture = new Fixture();
        var (job, session, qc) = await BeginBlockedQcAsync(fixture);
        try
        {
            fixture.Supervisor.PciIdentityProbeAsync = _ =>
            {
                Assert.True(session.Disposed);
                return Task.FromResult(new DeckCapture.Capture.DeckLinkPciCorrelationResult(true));
            };
            Assert.True((await fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None)).Available);
            var shutdown = fixture.Supervisor.StopAsync(CancellationToken.None);
            Assert.False(shutdown.IsCompleted);
            qc.Release.TrySetResult(); await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(JobState.Complete, (await fixture.JobAsync(job.Id)).State);
        }
        finally { qc.Release.TrySetResult(); }
    }
}
