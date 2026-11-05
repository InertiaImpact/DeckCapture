using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

public sealed partial class SupervisorTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "deckcapture-supervisor-tests-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeBackend Backend = new();
        internal readonly FakeDelivery Delivery = new();
        internal StateStore Store { get; }
        internal CaptureSupervisor Supervisor { get; private set; }

        internal Fixture(Action<StateStore, AppConfiguration>? seed = null)
        {
            Store = new(DirectoryPath);
            var config = ConfigurationRules.DevelopmentDefaults(DirectoryPath) with
            {
                EmergencyReserveBytes = 0,
                Capture = new() { ReservationBytesPerSecond = 100_000 },
                Transfer = new() { Windows = [] }
            };
            Store.Save("configuration", "current", config);
            seed?.Invoke(Store, config);
            var secrets = new SecretStore(new EphemeralDataProtectionProvider(), DirectoryPath);
            var notifications = new NotificationService(Store, secrets, NullLogger<NotificationService>.Instance);
            Supervisor = new(Store, Backend, Delivery, notifications, NullLogger<CaptureSupervisor>.Instance, DirectoryPath);
        }

        internal PrepareRecordingRequest Request(string channel = "ch1", string name = "Test master", Dictionary<string, string>? metadata = null,
            string endMode = "Off") => new(channel, name, 10, "local-test", "collection", metadata, EndMode: endMode);

        internal async Task<RecordingJob> JobAsync(string id)
        {
            var snapshot = JsonSerializer.SerializeToElement(await Supervisor.SnapshotAsync(), StateStore.Json);
            return snapshot.GetProperty("recordings").Deserialize<RecordingJob[]>(StateStore.Json)!.Single(job => job.Id == id);
        }

        internal async Task RestartAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None); Supervisor.Dispose();
            Supervisor = new(Store, Backend, Delivery, new NotificationService(Store,
                new SecretStore(new EphemeralDataProtectionProvider(), DirectoryPath), NullLogger<NotificationService>.Instance),
                NullLogger<CaptureSupervisor>.Instance, DirectoryPath);
        }

        internal async Task<RecordingJob> WaitForFinalizedAsync(string id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var job = await JobAsync(id);
                if (job.State is JobState.Complete or JobState.NeedsReview or JobState.Failed) return job;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Fake capture did not finalize.");
                await Task.Delay(10);
            }
        }

        internal async Task<RecordingJob> WaitForAsync(string id, Func<RecordingJob, bool> condition, double timeoutSeconds = 4)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < timeoutSeconds)
            {
                var job = await JobAsync(id);
                if (condition(job)) return job;
                if (job.State == JobState.Failed) throw new InvalidOperationException(job.Error);
                await Task.Delay(20);
            }
            throw new TimeoutException("Expected supervisor transition was not observed.");
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None);
            Supervisor.Dispose();
            // Connections are short-lived but SQLite's default pool retains idle
            // file handles; release them before removing this unique fixture root.
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class FakeBackend : ICaptureBackend
    {
        internal readonly Dictionary<string, FakeSession> Sessions = [];
        internal int OpenCalls;
        internal string? BlockedChannel;
        internal readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken OpeningToken;
        internal Action<CaptureOpenOptions>? BeforeOpen;
        internal readonly List<CaptureOpenOptions> OpenOptions = [];
        internal CaptureCapabilities Capabilities = new(true, false, "Unit-test fake");
        public Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);
        public async Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref OpenCalls);
            BeforeOpen?.Invoke(options);
            lock (OpenOptions) OpenOptions.Add(options);
            if (options.Channel.Id == BlockedChannel)
            {
                OpeningToken = cancellationToken;
                OpenEntered.TrySetResult();
                // Simulate a driver whose cancellation cleanup still needs to
                // finish before its physical-input reservation can be released.
                await ReleaseOpen.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }
            var session = new FakeSession();
            Sessions[options.Channel.Id] = session;
            return session;
        }
    }

    private sealed class FakeSession : ICaptureSession
    {
        private readonly TaskCompletionSource<CaptureResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? output;
        internal int BeginCalls;
        internal int StopCalls;
        internal int Duration;
        internal int PreRollSeconds;
        internal bool Disposed;
        internal TaskCompletionSource? DisposalEntered;
        internal TaskCompletionSource? ReleaseDisposal;
        internal CancellationToken RecordingToken;
        internal CaptureMetrics? MetricsOnStop;
        private CaptureMetrics metrics = new() { SignalLocked = true, AnalysisAvailable = true, AnalysisAgeSeconds = 0, IsBlack = false, BlackRatio = 0, AudioRmsDbfs = -20 };
        public CaptureMetrics Metrics { get => Volatile.Read(ref metrics); internal set => Volatile.Write(ref metrics, value); }
        public Task<CaptureResult> Completion => completion.Task;
        internal void CompleteMonitoring(string error) => completion.TrySetResult(new(false, null, 0, 0, 0, error));
        public Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken)
        {
            BeginCalls++; output = outputPath; Duration = durationSeconds; PreRollSeconds = preRollSeconds; RecordingToken = cancellationToken;
            return Task.CompletedTask;
        }
        public Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken)
        {
            Duration = totalDurationSeconds;
            return Task.CompletedTask;
        }
        public async Task<CaptureResult> StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            if (completion.Task.IsCompleted) return await completion.Task;
            // This is an explicit unit-test adapter, not a media acceptance test.
            await File.WriteAllTextAsync(output!, "Deterministic fake capture bytes", cancellationToken);
            var result = new CaptureResult(true, output, 1, 25, 0);
            if (MetricsOnStop is { } last) Metrics = last;
            completion.TrySetResult(result);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            DisposalEntered?.TrySetResult();
            if (ReleaseDisposal is { } release) await release.Task;
            Disposed = true; completion.TrySetResult(new(false, null, 0, 0, 0, "Unit-test input disposed."));
        }
    }

    private sealed class FakeDelivery : IFileDelivery
    {
        internal int Calls;
        internal Func<DeliveryTransferRequest, Func<DeliveryRates>, CancellationToken, Task<DeliveryResult>>? Handler;
        public Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates,
            IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Handler is not null) return Handler(request, rates, cancellationToken);
            throw new InvalidOperationException("Closed-schedule unit tests must not perform delivery.");
        }
    }

    [Fact]
    public async Task OnRequestMonitorNeedsNoRecordingMetadataAndStopsWithoutCreatingARecording()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        config.MetadataFields["station"] = new(true, true);
        await fixture.Supervisor.UpdateConfigurationAsync(config, CancellationToken.None);
        var monitor = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        Assert.Equal("Live", monitor.State); Assert.Equal("Monitor", monitor.Source);
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
        Assert.Equal(0, Assert.Single(fixture.Backend.OpenOptions).Profile.PreRollSeconds);
        Assert.True(Assert.Single(fixture.Backend.OpenOptions).MonitorOnly);
        Assert.Equal(0, Assert.Single(fixture.Backend.OpenOptions).Profile.MonitoringTimeoutMinutes);
        var again = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        Assert.Equal(monitor.Id, again.Id); Assert.Equal(1, fixture.Backend.OpenCalls);
        await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
        Assert.True(fixture.Backend.Sessions["ch1"].Disposed);
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
        await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidPreparePreservesMonitorAndValidPrepareReopensOnlyAfterOldInputCloses()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync(); config.MetadataFields["station"] = new(true, true);
        await fixture.Supervisor.UpdateConfigurationAsync(config, CancellationToken.None);
        var monitor = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        var original = fixture.Backend.Sessions["ch1"];
        Assert.Equal(400, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None))).StatusCode);
        Assert.False(original.Disposed); Assert.Equal(1, fixture.Backend.OpenCalls);
        fixture.Backend.BeforeOpen = _ => Assert.True(original.Disposed, "A second input opened before the monitor released its input.");
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(metadata: new() { ["station"] = "Unit test station" }), CancellationToken.None);
        Assert.NotEqual(monitor.Id, prepared.Id); Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.True(fixture.Backend.OpenOptions[0].MonitorOnly);
        Assert.False(fixture.Backend.OpenOptions[1].MonitorOnly);
        var attached = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        Assert.Equal(prepared.Id, attached.Id); Assert.Equal("RecordingJob", attached.Source);
        Assert.Equal(2, fixture.Backend.OpenCalls);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.StopMonitorAsync("ch1", CancellationToken.None))).StatusCode);
        Assert.False(fixture.Backend.Sessions["ch1"].Disposed);
    }

    [Fact]
    public async Task MonitorOpeningHoldsOwnershipUntilCancellationCleanupFinishes()
    {
        await using var fixture = new Fixture(); fixture.Backend.BlockedChannel = "ch1";
        var start = fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop = fixture.Supervisor.StopMonitorAsync("ch1", CancellationToken.None);
        var prepare = fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        await Task.Delay(50); Assert.Equal(1, fixture.Backend.OpenCalls); Assert.False(prepare.IsCompleted);
        fixture.Backend.ReleaseOpen.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(2)); await stop.WaitAsync(TimeSpan.FromSeconds(2));
        var prepared = await prepare.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JobState.Prepared, prepared.State); Assert.Equal(2, fixture.Backend.OpenCalls);
    }

    [Fact]
    public async Task MonitorExpiryIsRetriableAndRestartNeverReopensAnInput()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { Capture = config.Capture with { MonitoringTimeoutMinutes = 3 } }, CancellationToken.None);
        var first = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        Assert.Equal(3, Assert.Single(fixture.Backend.OpenOptions).Profile.MonitoringTimeoutMinutes);
        fixture.Backend.Sessions["ch1"].CompleteMonitoring("Monitoring expired after its configured 3-minute timeout.");
        JsonElement state = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(10);
            state = JsonSerializer.SerializeToElement(await fixture.Supervisor.SnapshotAsync(), StateStore.Json);
            if (state.GetProperty("channels")[0].GetProperty("monitoring").GetProperty("state").GetString() == "Failed" &&
                state.GetProperty("system").GetProperty("reservedBytes").GetInt64() == 0) break;
        }
        var descriptor = state.GetProperty("channels")[0].GetProperty("monitoring");
        Assert.Equal("Failed", descriptor.GetProperty("state").GetString());
        Assert.Contains("Start monitoring again", descriptor.GetProperty("error").GetString());
        Assert.Contains("3-minute timeout", descriptor.GetProperty("error").GetString());
        var second = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        var calls = fixture.Backend.OpenCalls;
        await fixture.RestartAsync();
        Assert.Equal(3, (await fixture.Supervisor.ConfigurationAsync()).Capture.MonitoringTimeoutMinutes);
        Assert.Equal(calls, fixture.Backend.OpenCalls);
        state = JsonSerializer.SerializeToElement(await fixture.Supervisor.SnapshotAsync(), StateStore.Json);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("channels")[0].GetProperty("monitoring").ValueKind);
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
    }

    [Fact]
    public async Task LiveFilesUseExactMonitorEpochAndPublicStateDoesNotExposeWorkerTransportPath()
    {
        await using var fixture = new Fixture();
        var monitor = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        var directory = Path.Combine(Assert.Single(fixture.Backend.OpenOptions).WorkingDirectory, "spool-" + Guid.NewGuid().ToString("N"));
        var hls = Path.Combine(directory, "preview-hls"); Directory.CreateDirectory(hls);
        var manifest = Path.Combine(hls, "index.m3u8");
        await File.WriteAllTextAsync(manifest, "#EXTM3U\n#EXTINF:1,\nindex0.ts\n");
        await File.WriteAllBytesAsync(Path.Combine(directory, "preview.jpg"), [255, 216]);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { LivePreviewPath = manifest, LivePreviewAvailable = true, PreviewPath = Path.Combine(directory, "preview.jpg") };
        Assert.NotNull(await fixture.Supervisor.ReadLivePreviewAsync("ch1", monitor.Id, "index.m3u8", CancellationToken.None));
        Assert.Null(await fixture.Supervisor.ReadLivePreviewAsync("ch2", monitor.Id, "index.m3u8", CancellationToken.None));
        Assert.NotNull(await fixture.Supervisor.PreviewPathAsync("ch1"));
        var state = JsonSerializer.SerializeToElement(await fixture.Supervisor.SnapshotAsync(), StateStore.Json);
        var metrics = state.GetProperty("channels")[0].GetProperty("metrics");
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("livePreviewPath").ValueKind);
        Assert.Contains(monitor.Id, metrics.GetProperty("livePreviewUrl").GetString());
        await fixture.Supervisor.StopMonitorAsync("ch1", CancellationToken.None);
        Assert.Null(await fixture.Supervisor.ReadLivePreviewAsync("ch1", monitor.Id, "index.m3u8", CancellationToken.None));
        Assert.Null(await fixture.Supervisor.PreviewPathAsync("ch1"));
    }

    [Fact]
    public async Task RemoteDiagnosticsIncludesMonitorOnlyEncoderEvidenceWithoutRecordingJobs()
    {
        await using var fixture = new Fixture();
        var monitor = await fixture.Supervisor.StartMonitorAsync("ch1", CancellationToken.None);
        var root = Assert.Single(fixture.Backend.OpenOptions).WorkingDirectory;
        await File.WriteAllTextAsync(Path.Combine(root, "encoder.log"), "Preview preflight h264_mf: HRESULT 0x80004005; fixture codec failure.");
        fixture.Backend.Sessions["ch1"].CompleteMonitoring("Fixture monitor ended.");
        await Task.Delay(40);
        using var diagnostics = new DevelopmentDiagnostics(fixture.DirectoryPath);
        var bundle = await fixture.Supervisor.DiagnosticsBundleAsync(diagnostics, CancellationToken.None);
        using var archive = new ZipArchive(new MemoryStream(bundle.Content), ZipArchiveMode.Read);
        var entry = archive.GetEntry($"capture/{monitor.Id}/encoder.txt");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.Contains("HRESULT 0x80004005", await reader.ReadToEndAsync());
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
    }

    [Fact]
    public async Task PrepareFiltersHiddenMetadataAndCopiesCallerValues()
    {
        await using var fixture = new Fixture();
        var input = new Dictionary<string, string> { ["station"] = "  WXYZ  ", ["tapeId"] = "hidden", ["unknown"] = "ignored" };
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(metadata: input), CancellationToken.None);
        Assert.Equal("WXYZ", prepared.Metadata["station"]);
        Assert.Single(prepared.Metadata);
        input["station"] = "Changed request";
        prepared.Metadata["station"] = "Changed response";
        Assert.Equal("WXYZ", (await fixture.JobAsync(prepared.Id)).Metadata["station"]);
    }

    [Fact]
    public async Task RevisionConflictsCannotStartOrStopCaptureTwice()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        var stale = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision + 1, CancellationToken.None));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(0, session.BeginCalls);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        Assert.Equal(JobState.Recording, recording.State);
        await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.StopAsync(recording.Id, prepared.Revision, CancellationToken.None));
        Assert.Equal(0, session.StopCalls);
        var finalizing = await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        Assert.Equal(JobState.Finalizing, finalizing.State);
        var finalized = await fixture.WaitForFinalizedAsync(recording.Id);
        Assert.Equal(JobState.Complete, finalized.State);
        Assert.Equal(1, session.BeginCalls);
        Assert.Equal(1, session.StopCalls);
        Assert.Equal(0, finalized.ReservedBytes);
        Assert.NotEmpty(finalized.Artifacts);
        foreach (var file in finalized.Artifacts)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(finalized.PackageDirectory, file.RelativePath));
            Assert.Equal(bytes.LongLength, file.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256);
        }
        Assert.Equal(DeliveryState.Queued, fixture.Store.ReadAll<DeliveryJob>("delivery").Single().State);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task BrowserRequestCancellationDoesNotStopStartedRecording()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        using var request = new CancellationTokenSource();
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, request.Token);
        request.Cancel();
        Assert.False(fixture.Backend.Sessions["ch1"].RecordingToken.IsCancellationRequested);
        Assert.Equal(JobState.Recording, (await fixture.JobAsync(recording.Id)).State);
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(recording.Id)).State);
    }

    [Fact]
    public async Task SnapshotChannelJobDoesNotChangeAfterGateIsReleased()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var snapshot = await fixture.Supervisor.SnapshotAsync();
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(snapshot, StateStore.Json);
        var channel = json.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("configuration").GetProperty("id").GetString() == "ch1");
        Assert.Equal("Prepared", channel.GetProperty("job").GetProperty("state").GetString());
        Assert.Equal(JobState.Recording, (await fixture.JobAsync(recording.Id)).State);
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(recording.Id)).State);
    }

    [Fact]
    public async Task CancelingOnePreparedChannelLeavesOtherInputOpen()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var second = await fixture.Supervisor.PrepareAsync(fixture.Request("ch2", "Second master"), CancellationToken.None);
        var canceled = await fixture.Supervisor.CancelAsync(first.Id, first.Revision, CancellationToken.None);
        Assert.Equal(JobState.Canceled, canceled.State);
        Assert.Equal(0, canceled.ReservedBytes);
        Assert.True(fixture.Backend.Sessions["ch1"].Disposed);
        Assert.False(fixture.Backend.Sessions["ch2"].Disposed);
        Assert.Equal(JobState.Prepared, (await fixture.JobAsync(second.Id)).State);
        await fixture.Supervisor.PrepareAsync(fixture.Request(name: "Replacement master"), CancellationToken.None);
        Assert.Equal(3, fixture.Backend.OpenCalls);
    }

    [Fact]
    public async Task ConfigurationChangesKeepPreparedSnapshotAndHiddenMetadataOutOfNextJob()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(metadata: new() { ["station"] = "WXYZ" }), CancellationToken.None);
        var candidate = await fixture.Supervisor.ConfigurationAsync();
        candidate.MetadataFields["station"] = new(false, false);
        candidate = candidate with { Capture = candidate.Capture with { PreRollSeconds = 7 } };
        await fixture.Supervisor.UpdateConfigurationAsync(candidate, CancellationToken.None);
        candidate.MetadataFields.Clear();
        var earlier = await fixture.JobAsync(prepared.Id);
        Assert.Equal(5, earlier.Configuration.Capture.PreRollSeconds);
        Assert.True(earlier.Configuration.MetadataFields["station"].Visible);
        Assert.Equal("WXYZ", earlier.Metadata["station"]);
        var next = await fixture.Supervisor.PrepareAsync(fixture.Request("ch2", "Next master", new() { ["station"] = "Must be ignored" }), CancellationToken.None);
        Assert.Equal(7, next.Configuration.Capture.PreRollSeconds);
        Assert.Empty(next.Metadata);
    }

    [Fact]
    public async Task GlobalExperimentalDisableClearsActiveJobFlagButKeepsDuration()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { ExperimentalEndDetection = true }, CancellationToken.None);
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(endMode: "Stop"), CancellationToken.None);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        Assert.True(recording.ExperimentalEndDetectionActive);
        config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { ExperimentalEndDetection = false }, CancellationToken.None);
        var updated = await fixture.JobAsync(recording.Id);
        Assert.False(updated.ExperimentalEndDetectionActive);
        Assert.Equal(JobState.Recording, updated.State);
        Assert.Equal(recording.DurationSeconds, updated.DurationSeconds);
        Assert.True(updated.Revision > recording.Revision);
        await fixture.Supervisor.StopAsync(updated.Id, updated.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(updated.Id)).State);
    }

    [Fact]
    public async Task RestartMarksUnfinishedRecordingInterruptedAndPreservesBytes()
    {
        const string id = "interrupted-fixture";
        string? retained = null;
        await using var fixture = new Fixture((store, config) =>
        {
            var package = Path.Combine(config.CacheRoot, "jobs", id);
            Directory.CreateDirectory(package);
            retained = Path.Combine(package, "partial.mkv");
            File.WriteAllText(retained, "Retained interrupted capture bytes");
            store.Save("recording", id, new RecordingJob { Id = id, ChannelId = "ch1", FileName = "Interrupted", PackageDirectory = package,
                State = JobState.Recording, Revision = 9, ReservedBytes = 100_000, Configuration = config });
        });
        var recovered = await fixture.JobAsync(id);
        Assert.Equal(JobState.Interrupted, recovered.State);
        Assert.Equal(10, recovered.Revision);
        Assert.Equal(0, recovered.ReservedBytes);
        Assert.Contains("not resumed", recovered.Error!);
        Assert.Equal("Retained interrupted capture bytes", await File.ReadAllTextAsync(retained!));
        Assert.Equal(0, fixture.Backend.OpenCalls);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task SlowInputOpenAllowsSnapshotsAndAnotherChannelToPrepare()
    {
        await using var fixture = new Fixture();
        fixture.Backend.BlockedChannel = "ch1";
        var opening = fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        try
        {
            await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var snapshot = await fixture.Supervisor.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(snapshot);
            var other = await fixture.Supervisor.PrepareAsync(fixture.Request("ch2", "Other input"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(JobState.Prepared, other.State);
            Assert.False(opening.IsCompleted);
        }
        finally { fixture.Backend.ReleaseOpen.TrySetResult(); }
        Assert.Equal(JobState.Prepared, (await opening.WaitAsync(TimeSpan.FromSeconds(2))).State);
    }

    [Fact]
    public async Task CanceledOpeningKeepsPhysicalInputReservedUntilCleanupCompletes()
    {
        await using var fixture = new Fixture();
        fixture.Backend.BlockedChannel = "ch1";
        var opening = fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        try
        {
            await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var pending = fixture.Store.ReadAll<RecordingJob>("recording").Single();
            var canceled = await fixture.Supervisor.CancelAsync(pending.Id, pending.Revision, CancellationToken.None);
            Assert.Equal(JobState.Canceled, canceled.State);
            Assert.True(fixture.Backend.OpeningToken.IsCancellationRequested);
            var conflict = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request(name: "Replacement"), CancellationToken.None));
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal(1, fixture.Backend.OpenCalls);
            var config = await fixture.Supervisor.ConfigurationAsync();
            config = config with { Channels = config.Channels.Select(channel => channel.Id == "ch1" ? channel with { DeviceName = "Remapped device" } : channel).ToList() };
            var remap = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.UpdateConfigurationAsync(config, CancellationToken.None));
            Assert.Equal(409, remap.StatusCode);
        }
        finally { fixture.Backend.ReleaseOpen.TrySetResult(); }
        await Assert.ThrowsAsync<CommandException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(2)));
        var replacement = await fixture.Supervisor.PrepareAsync(fixture.Request(name: "Replacement"), CancellationToken.None);
        Assert.Equal(JobState.Prepared, replacement.State);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsAnInputThatIsStillOpening()
    {
        await using var fixture = new Fixture();
        fixture.Backend.BlockedChannel = "ch1";
        var opening = fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var shutdown = fixture.Supervisor.StopAsync(CancellationToken.None);
        try
        {
            // StopAsync cancels the opening synchronously before its first wait.
            Assert.True(fixture.Backend.OpeningToken.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
        }
        finally { fixture.Backend.ReleaseOpen.TrySetResult(); }
        await Assert.ThrowsAsync<CommandException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(2)));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(fixture.Backend.Sessions);
        var recovered = fixture.Store.ReadAll<RecordingJob>("recording").Single();
        Assert.Equal(JobState.Failed, recovered.State);
        Assert.Equal(0, recovered.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverrideAdmitsClosedWindowWorkWithoutBypassingItsBandwidthLimit(bool globalOverride)
    {
        await using var fixture = new Fixture((store, config) =>
        {
            store.Save("recording", "complete-source", new RecordingJob { Id = "complete-source", ChannelId = "ch1", FileName = "Queued master",
                State = JobState.Complete, Configuration = config });
            store.Save("delivery", "queued-delivery", new DeliveryJob { Id = "queued-delivery", RecordingId = "complete-source", FileName = "Queued master",
                SourceDirectory = config.CacheRoot, DestinationRoot = config.Destinations[0].Root, State = DeliveryState.Queued, Override = !globalOverride });
        });
        var captured = new TaskCompletionSource<DeliveryRates>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Delivery.Handler = (request, rates, cancellationToken) =>
        {
            captured.TrySetResult(rates());
            return Task.FromResult(new DeliveryResult(Path.Combine(request.DestinationRoot, request.JobId), true));
        };
        if (globalOverride) await fixture.Supervisor.SetTransferOverrideAsync(true, 12, CancellationToken.None);
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        var actual = await captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(config.Transfer.OutsideWindowMiBps, actual.PerTransferMiBps);
        Assert.Equal(config.Transfer.AggregateMiBps, actual.AggregateMiBps);
        Assert.Equal(1, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task WaitingForContentRequiresNewTransitionAndFullConfirmationBeforePreRollStart()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { Detection = config.Detection with { StartConfirmationSeconds = 1 } }, CancellationToken.None);
        var waiting = await fixture.Supervisor.PrepareAsync(fixture.Request() with { WaitForContent = true }, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        // Input was already playing when armed. Even longer than the complete
        // confirmation interval must not invent a fresh content transition.
        await Task.Delay(1300);
        Assert.Equal(JobState.Waiting, (await fixture.JobAsync(waiting.Id)).State);
        Assert.Equal(0, session.BeginCalls);
        session.Metrics = session.Metrics with { IsBlack = true, BlackRatio = 1, AudioRmsDbfs = -100 };
        await Task.Delay(350); // Allow a full 250-ms supervisor tick to see leader.
        session.Metrics = session.Metrics with { IsBlack = false, BlackRatio = 0, AudioRmsDbfs = -20 };
        await Task.Delay(400);
        Assert.Equal(JobState.Waiting, (await fixture.JobAsync(waiting.Id)).State);
        Assert.Equal(0, session.BeginCalls);
        var recording = await fixture.WaitForAsync(waiting.Id, job => job.State == JobState.Recording);
        Assert.Equal(1, session.BeginCalls);
        Assert.Equal(config.Capture.PreRollSeconds, session.PreRollSeconds);
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(recording.Id)).State);
    }

    [Fact]
    public async Task UnknownHardwareLockAllowsFreshContentButReportedNoInputAndStaleAnalysisBlockIt()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { Detection = config.Detection with { StartConfirmationSeconds = 1 } }, CancellationToken.None);
        var waiting = await fixture.Supervisor.PrepareAsync(fixture.Request() with { WaitForContent = true }, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { SignalLocked = null, InputSignalPresent = false, IsBlack = true, BlackRatio = 1, AudioRmsDbfs = -100 };
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(350);
        session.Metrics = session.Metrics with { IsBlack = false, BlackRatio = 0, AudioRmsDbfs = -20 };
        await Task.Delay(1300);
        Assert.Equal(JobState.Waiting, (await fixture.JobAsync(waiting.Id)).State);
        Assert.Equal(0, session.BeginCalls);
        session.Metrics = session.Metrics with { InputSignalPresent = null, AnalysisAgeSeconds = 10 };
        await Task.Delay(1300);
        Assert.Equal(JobState.Waiting, (await fixture.JobAsync(waiting.Id)).State);
        session.Metrics = session.Metrics with { AnalysisAgeSeconds = 0 };
        var recording = await fixture.WaitForAsync(waiting.Id, job => job.State == JobState.Recording);
        await Task.Delay(350);
        Assert.DoesNotContain((await fixture.JobAsync(waiting.Id)).Warnings, warning => warning.Contains("not locked", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, session.BeginCalls);
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
    }

    [Fact]
    public async Task ExplicitNoInputProducesAccurateWarningWhileUnknownLockDoesNot()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { SignalLocked = null, InputSignalPresent = null };
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        Assert.DoesNotContain((await fixture.JobAsync(recording.Id)).Warnings, warning => warning.Contains("input", StringComparison.OrdinalIgnoreCase));
        session.Metrics = session.Metrics with { InputSignalPresent = false, InputSignalStatusSource = "FfmpegDeckLinkFrameFlag" };
        var warned = await fixture.WaitForAsync(recording.Id, job => job.Warnings.Any(warning => warning.Contains("FFmpeg reported no input signal", StringComparison.Ordinal)));
        Assert.DoesNotContain(warned.Warnings, warning => warning.Contains("could not be verified", StringComparison.Ordinal));
        await fixture.Supervisor.StopAsync(recording.Id, warned.Revision, CancellationToken.None);
    }

    [Fact]
    public async Task IntegrityWarningsReportedOnlyAtStopPreventCleanCompletionEvenWithZeroProgressDrops()
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.MetricsOnStop = session.Metrics with { InputBufferOverrun = true, OutputFrameDuplication = true,
            DroppedFrames = 0, AnalysisAvailable = false, Warning = "Monitoring failed: Incomplete monitor samples." };
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        var finalized = await fixture.WaitForFinalizedAsync(recording.Id);
        Assert.Equal(JobState.NeedsReview, finalized.State);
        Assert.Contains(finalized.Warnings, warning => warning.Contains("input buffer overrun", StringComparison.Ordinal));
        Assert.Contains(finalized.Warnings, warning => warning.Contains("duplicated output frames", StringComparison.Ordinal));
        Assert.DoesNotContain(finalized.Warnings, warning => warning == "Capture pipeline reported dropped frames.");
        Assert.Equal(DeliveryState.Held, Assert.Single(fixture.Store.ReadAll<DeliveryJob>("delivery")).State);
        using var qc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(finalized.PackageDirectory, "qc-summary.json")));
        Assert.Equal(0, qc.RootElement.GetProperty("droppedFrames").GetInt64());
        Assert.Contains(qc.RootElement.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("input buffer overrun", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitoringExpiryKeepsItsReasonAndRequiresFreshPreparation(bool waitingForContent)
    {
        await using var fixture = new Fixture();
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request() with { WaitForContent = waitingForContent }, CancellationToken.None);
        const string reason = "Monitoring expired after 15 armed minutes without recording. Prepare a new job; original spool retained for inspection.";
        fixture.Backend.Sessions["ch1"].CompleteMonitoring(reason);
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        var failed = await fixture.WaitForAsync(prepared.Id, job => job.State == JobState.Failed);
        Assert.Equal(reason, failed.Error);
        Assert.Equal(0, failed.ReservedBytes);
        Assert.Equal(0, fixture.Backend.Sessions["ch1"].BeginCalls);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.StartAsync(failed.Id, failed.Revision, CancellationToken.None))).StatusCode);
    }

    [Fact]
    public async Task ExperimentalBlackAndSilenceWarnThenHonorGraceBeforeStopping()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { ExperimentalEndDetection = true,
            Detection = config.Detection with { MinimumRecordedSeconds = 0, BlackSeconds = 1, GraceSeconds = 1 } }, CancellationToken.None);
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(endMode: "Stop"), CancellationToken.None);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { SignalLocked = null, InputSignalPresent = null, IsBlack = true, BlackRatio = 1, AudioRmsDbfs = -100 };
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        var warned = await fixture.WaitForAsync(recording.Id, job => job.Warnings.Any(warning => warning.Contains("Suspected tape ending", StringComparison.Ordinal)));
        Assert.Equal(JobState.Recording, warned.State);
        Assert.Equal(0, session.StopCalls);
        var grace = Stopwatch.StartNew();
        var finalized = await fixture.WaitForFinalizedAsync(recording.Id);
        Assert.True(grace.Elapsed.TotalSeconds >= .7, "Heuristic finalization skipped its one-second grace interval.");
        Assert.Equal(JobState.NeedsReview, finalized.State);
        Assert.Equal("Experimental tape-end stop", finalized.StopReason);
        Assert.Equal(1, session.StopCalls);
        Assert.Equal(DeliveryState.Held, fixture.Store.ReadAll<DeliveryJob>("delivery").Single().State);
    }

    [Fact]
    public async Task GlobalDisableCancelsPendingHeuristicStopWhileDurationStillFinalizes()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { ExperimentalEndDetection = true,
            Detection = config.Detection with { MinimumRecordedSeconds = 0, BlackSeconds = 1, GraceSeconds = 1 } }, CancellationToken.None);
        // Three seconds leaves room to observe cancellation beyond the heuristic
        // deadline (one second black + one second grace), then the hard deadline.
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(endMode: "Stop") with { DurationSeconds = 3 }, CancellationToken.None);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        var session = fixture.Backend.Sessions["ch1"];
        session.Metrics = session.Metrics with { SignalLocked = null, InputSignalPresent = null, IsBlack = true, BlackRatio = 1, AudioRmsDbfs = -100 };
        await fixture.Supervisor.StartAsync(CancellationToken.None);
        await fixture.WaitForAsync(recording.Id, job => job.Warnings.Any(warning => warning.Contains("Suspected tape ending", StringComparison.Ordinal)));
        config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { ExperimentalEndDetection = false }, CancellationToken.None);
        await Task.Delay(1250);
        var continuing = await fixture.JobAsync(recording.Id);
        Assert.Equal(JobState.Recording, continuing.State);
        Assert.False(continuing.ExperimentalEndDetectionActive);
        Assert.Equal(0, session.StopCalls);
        var finalized = await fixture.WaitForFinalizedAsync(recording.Id);
        Assert.Equal("Duration reached", finalized.StopReason);
        Assert.Equal(JobState.NeedsReview, finalized.State);
        Assert.Equal(1, session.StopCalls);
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData(".spool")]
    public async Task CacheReparseDirectoryCannotRedirectPreparationOutsideApprovedRoot(string folder)
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        var escape = Path.Combine(fixture.DirectoryPath, "reparse-target");
        Directory.CreateDirectory(escape);
        Directory.CreateSymbolicLink(Path.Combine(config.CacheRoot, folder), escape);
        await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None));
        Assert.Equal(0, fixture.Backend.OpenCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(escape));
    }

    [Fact]
    public async Task IdleMaintenanceLeaseBlocksPreparationUntilExplicitlyReleased()
    {
        await using var fixture = new Fixture();
        var lease = JsonSerializer.SerializeToElement(await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None), StateStore.Json);
        Assert.True(lease.GetProperty("enabled").GetBoolean());
        Assert.True(lease.GetProperty("until").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var blocked = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None));
        Assert.Equal(409, blocked.StatusCode);
        Assert.Equal(0, fixture.Backend.OpenCalls);
        await fixture.Supervisor.SetMaintenanceAsync(false, CancellationToken.None);
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        var recording = await fixture.Supervisor.StartAsync(prepared.Id, prepared.Revision, CancellationToken.None);
        Assert.Equal(JobState.Recording, recording.State);
        await fixture.Supervisor.StopAsync(recording.Id, recording.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(recording.Id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedBusyMaintenanceLeaseDoesNotDisableCapture(bool alreadyRecording)
    {
        await using var fixture = new Fixture();
        var job = await fixture.Supervisor.PrepareAsync(fixture.Request(), CancellationToken.None);
        if (alreadyRecording) job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        var rejected = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None));
        Assert.Equal(409, rejected.StatusCode);
        if (!alreadyRecording) job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        Assert.Equal(JobState.Recording, (await fixture.JobAsync(job.Id)).State);
        var other = await fixture.Supervisor.PrepareAsync(fixture.Request("ch2", "Still admitted"), CancellationToken.None);
        Assert.Equal(JobState.Prepared, other.State);
        await fixture.Supervisor.StopAsync(job.Id, job.Revision, CancellationToken.None);
        Assert.Equal(JobState.Complete, (await fixture.WaitForFinalizedAsync(job.Id)).State);
    }
}
