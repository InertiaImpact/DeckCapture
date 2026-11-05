using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed partial class SupervisorTests
{
    [Fact]
    public async Task IdleProbeDrainsAutomaticMonitorAndResumesAfterReservationEnds()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var old = fixture.Backend.Sessions["ch1"];
        fixture.Supervisor.PciIdentityProbeAsync = async token =>
        {
            Assert.True(old.Disposed);
            clock.Advance(60);
            await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
            Assert.Equal(1, fixture.Backend.OpenCalls);
            return new DeckCapture.Capture.DeckLinkPciCorrelationResult(true);
        };
        await fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
    }

    private sealed class MonitorClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private static async Task<MonitorClock> ConfigureAutomaticAsync(Fixture fixture, int channels = 1)
    {
        var clock = new MonitorClock(); fixture.Supervisor.MonitorTimeProvider = clock;
        var configuration = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(configuration with
        {
            Channels = configuration.Channels.Take(channels).ToList(),
            Capture = configuration.Capture with { MonitoringTimeoutMinutes = 5 }
        }, CancellationToken.None);
        return clock;
    }

    [Fact]
    public async Task HostedStartupAutomaticallyOpensConfiguredChannelsWithoutRecordingMetadata()
    {
        await using var fixture = new Fixture();
        await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && Volatile.Read(ref fixture.Backend.OpenCalls) == 0; i++) await Task.Delay(10);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
        var options = Assert.Single(fixture.Backend.OpenOptions);
        Assert.True(options.MonitorOnly); Assert.Equal(0, options.Profile.MonitoringTimeoutMinutes);
    }

    [Fact]
    public async Task AutomaticOpensAreSequentialAndStaggeredAcrossChannels()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture, 2);
        fixture.Backend.BlockedChannel = "ch1";
        var first = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(5);
        var concurrent = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        fixture.Backend.ReleaseOpen.TrySetResult();
        await Task.WhenAll(first, concurrent).WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        clock.Advance(1); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.Equal(new[] { "ch1", "ch2" }, fixture.Backend.OpenOptions.Select(options => options.Channel.Id));
    }

    [Theory]
    [InlineData(true, 15)]
    [InlineData(false, 30)]
    public async Task FailedAutomaticOpenBacksOffThenRetriesGenuineInput(bool capabilityAvailable, int retrySeconds)
    {
        await using var fixture = new Fixture();
        fixture.Backend.Capabilities = new(capabilityAvailable, false, "Test fixture");
        var clock = await ConfigureAutomaticAsync(fixture);
        var noSignal = true;
        fixture.Backend.BeforeOpen = _ => { if (noSignal) throw new IOException("Cannot Autodetect input stream or No signal"); };
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        clock.Advance(retrySeconds - 1); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        noSignal = false; clock.Advance(1); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.False(fixture.Backend.Sessions["ch1"].Disposed);
    }

    [Fact]
    public async Task ExistingPreparedAndRecordingSessionsAreNeverReopenedByAutomaticMonitoring()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { InputSignalPresent = false };
        clock.Advance(600); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls); Assert.False(session.Disposed);
        Assert.Equal(JobState.Recording, (await fixture.JobAsync(recording.Id)).State);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None))).StatusCode);
        Assert.False(session.Disposed);
    }

    [Fact]
    public async Task MaintenanceDrainsAutomaticMonitorsAndPausesAttemptsUntilReleased()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var old = fixture.Backend.Sessions["ch1"];
        await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
        Assert.True(old.Disposed);
        clock.Advance(60); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
        await fixture.Supervisor.SetMaintenanceAsync(false, CancellationToken.None);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
    }

    [Fact]
    public async Task MaintenanceCancelsAnOpeningAutomaticMonitorBeforeAdmittingNewWork()
    {
        await using var fixture = new Fixture();
        await ConfigureAutomaticAsync(fixture);
        fixture.Backend.BlockedChannel = "ch1";
        var attempt = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var maintenance = fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
        Assert.True(fixture.Backend.OpeningToken.IsCancellationRequested);
        Assert.False(maintenance.IsCompleted);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None))).StatusCode);
        fixture.Backend.ReleaseOpen.TrySetResult();
        await Task.WhenAll(attempt, maintenance).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Backend.OpenCalls);
    }

    [Fact]
    public async Task ConfigurationChangeClosesOnlyIdleMonitorAndReopensCurrentSource()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var old = fixture.Backend.Sessions["ch1"];
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { Channels = [config.Channels[0] with { DeviceName = "new exact source" }] }, CancellationToken.None);
        Assert.True(old.Disposed);
        clock.Advance(1); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.Equal("new exact source", fixture.Backend.OpenOptions.Last().Channel.DeviceName);
    }

    [Fact]
    public async Task ConfigurationChangeCancelsOldAutomaticOpenAndWaitsForOwnershipRelease()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        fixture.Backend.BlockedChannel = "ch1";
        var attempt = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var config = await fixture.Supervisor.ConfigurationAsync();
        var changing = fixture.Supervisor.UpdateConfigurationAsync(config with { Channels = [config.Channels[0] with { DeviceName = "replacement source" }] }, CancellationToken.None);
        Assert.True(fixture.Backend.OpeningToken.IsCancellationRequested);
        Assert.False(changing.IsCompleted); Assert.Equal(1, fixture.Backend.OpenCalls);
        fixture.Backend.ReleaseOpen.TrySetResult();
        await Task.WhenAll(attempt, changing).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(30); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.Equal("replacement source", fixture.Backend.OpenOptions.Last().Channel.DeviceName);
    }

    [Fact]
    public async Task ShutdownCancelsAutomaticOpenAndPreventsFurtherAttempts()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        fixture.Backend.BlockedChannel = "ch1";
        var attempt = fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shutdown = fixture.Supervisor.StopAsync(CancellationToken.None);
        Assert.True(fixture.Backend.OpeningToken.IsCancellationRequested);
        fixture.Backend.ReleaseOpen.TrySetResult();
        await Task.WhenAll(attempt, shutdown).WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(60); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(1, fixture.Backend.OpenCalls);
    }

    [Fact]
    public async Task PersistentExplicitNoInputRecoversOnlyIdleAutomaticSessionAndUnknownDoesNotChurn()
    {
        await using var fixture = new Fixture();
        var clock = await ConfigureAutomaticAsync(fixture);
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        var old = fixture.Backend.Sessions["ch1"];
        old.Metrics = old.Metrics with { InputSignalPresent = null, SignalLocked = null, AnalysisAvailable = false };
        clock.Advance(60); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.False(old.Disposed);
        old.Metrics = old.Metrics with { InputSignalPresent = false };
        await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        clock.Advance(14); await fixture.Supervisor.RunAutomaticMonitoringPassAsync(); Assert.False(old.Disposed);
        clock.Advance(1); await fixture.Supervisor.RunAutomaticMonitoringPassAsync(); Assert.True(old.Disposed);
        clock.Advance(15); await fixture.Supervisor.RunAutomaticMonitoringPassAsync();
        Assert.Equal(2, fixture.Backend.OpenCalls);
    }
}
