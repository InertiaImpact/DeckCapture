using System.Text.Json;
using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

public sealed class InputAssignmentTests
{
    private const string InputA = "opaque O'Brien [handle] -i keep-one";
    private const string InputB = "opaque-device-B";
    private const string InputC = "opaque-device-C";

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "deckcapture-input-assignment-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeBackend Backend = new();
        internal StateStore Store { get; }
        internal CaptureSupervisor Supervisor { get; }
        internal Fixture()
        {
            Store = new(DirectoryPath);
            Store.Save("configuration", "current", ConfigurationRules.DevelopmentDefaults(DirectoryPath) with
            {
                EmergencyReserveBytes = 0, Capture = new() { ReservationBytesPerSecond = 100_000 },
                Transfer = new() { Windows = [] },
                Channels = [new() { Id = "ch2", Label = "Second row first", SourceKind = "DeckLink", DeviceName = InputB },
                    new() { Id = "ch1", Label = "First physical assignment", SourceKind = "DeckLink", DeviceName = InputA }]
            });
            Supervisor = NewSupervisor();
        }
        internal CaptureSupervisor NewSupervisor()
        {
            var secrets = new SecretStore(new EphemeralDataProtectionProvider(), DirectoryPath);
            return new(Store, Backend, new NoDelivery(), new NotificationService(Store, secrets, NullLogger<NotificationService>.Instance),
                NullLogger<CaptureSupervisor>.Instance, DirectoryPath);
        }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None);
            Supervisor.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class FakeBackend : ICaptureBackend
    {
        internal int OpenCalls;
        public Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken) => Task.FromResult(new CaptureCapabilities(true, true, "Explicit assignment unit-test adapter"));
        public Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken)
        {
            OpenCalls++;
            return Task.FromResult<ICaptureSession>(new FakeSession());
        }
    }
    private sealed class FakeSession : ICaptureSession
    {
        private readonly TaskCompletionSource<CaptureResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureMetrics Metrics => new() { SignalLocked = true };
        public Task<CaptureResult> Completion => completion.Task;
        public Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CaptureResult> StopAsync(CancellationToken cancellationToken) => Task.FromResult(new CaptureResult(false, null, 0, 0, 0, "Explicit assignment fixture has no media; no capture success is claimed."));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoDelivery : IFileDelivery
    {
        public Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates, IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("Assignment tests never deliver media.");
    }

    [Fact]
    public async Task ExactInputNamesAndConfiguredRowOrderSurviveSaveExportAndSupervisorRestart()
    {
        await using var fixture = new Fixture();
        var candidate = await fixture.Supervisor.ConfigurationAsync();
        candidate = candidate with { Channels = candidate.Channels.Select(channel => channel.Id == "ch1" ? channel with { DeviceName = InputA + " [saved]", FormatCode = "Hi50" } : channel).ToList() };
        var saved = await fixture.Supervisor.UpdateConfigurationAsync(candidate, CancellationToken.None);
        var exported = JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(saved, StateStore.Json), StateStore.Json)!;
        Assert.Equal(new[] { "ch2", "ch1" }, exported.Channels.Select(channel => channel.Id));
        Assert.Equal(InputA + " [saved]", exported.Channels.Single(channel => channel.Id == "ch1").DeviceName);
        var restarted = fixture.NewSupervisor();
        try
        {
            var loaded = await restarted.ConfigurationAsync();
            Assert.Equal(new[] { "ch2", "ch1" }, loaded.Channels.Select(channel => channel.Id));
            var channel = loaded.Channels.Single(channel => channel.Id == "ch1");
            Assert.Equal(InputA + " [saved]", channel.DeviceName);
            Assert.Equal("Hi50", channel.FormatCode);
            Assert.Equal("DeckLink", channel.SourceKind);
            Assert.Equal(0, fixture.Backend.OpenCalls);
        }
        finally { await restarted.StopAsync(CancellationToken.None); restarted.Dispose(); }
    }

    [Fact]
    public async Task DiscoveryReorderingAndMissingHardwareNeverRewriteSavedAssignments()
    {
        await using var fixture = new Fixture();
        var mode = 0;
        fixture.Supervisor.InputDiscoveryService = new InputDiscovery((executable, arguments, cancellationToken) =>
        {
            if (arguments.Contains("-devices")) return Task.FromResult(new DiscoveryProcessResult(0, "Devices:\n D decklink Blackmagic DeckLink input\n", ""));
            var names = mode == 0 ? new[] { InputA, InputB } : mode == 1 ? new[] { InputB, InputA } : [];
            var text = "Auto-detected sources for decklink:\n" + string.Join('\n', names.Select(name => $"  {name} [Descriptive label is not the input key] (video, audio)"));
            return Task.FromResult(new DiscoveryProcessResult(0, text, ""));
        });
        var before = JsonSerializer.Serialize(await fixture.Supervisor.ConfigurationAsync(), StateStore.Json);
        var first = await fixture.Supervisor.DiscoverInputsAsync(CancellationToken.None);
        mode = 1;
        var reordered = await fixture.Supervisor.DiscoverInputsAsync(CancellationToken.None);
        Assert.Equal(InputA, first.Devices[0].Name);
        Assert.Equal(InputB, reordered.Devices[0].Name);
        mode = 2;
        var missing = await fixture.Supervisor.DiscoverInputsAsync(CancellationToken.None);
        Assert.Empty(missing.Devices);
        Assert.NotNull(missing.Error);
        Assert.Equal(before, JsonSerializer.Serialize(await fixture.Supervisor.ConfigurationAsync(), StateStore.Json));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(fixture.Store.ReadAll<AppConfiguration>("configuration")), StateStore.Json));
        Assert.Equal(0, fixture.Backend.OpenCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveInputCannotBeRemappedWhileAnIdleChannelCanChangeWithoutOpeningAnotherInput(bool recording)
    {
        await using var fixture = new Fixture();
        var job = await fixture.Supervisor.PrepareAsync(new("ch1", "Assignment fixture", 30, "local-test", "tests"), CancellationToken.None);
        if (recording) job = await fixture.Supervisor.StartAsync(job.Id, job.Revision, CancellationToken.None);
        var before = await fixture.Supervisor.ConfigurationAsync();
        var invalid = before with { Channels = before.Channels.Select(channel => channel.Id == "ch1" ? channel with { DeviceName = InputC } : channel).ToList() };
        var historyCount = fixture.Store.ReadAll<JsonElement>("configuration-history").Count;
        var conflict = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.UpdateConfigurationAsync(invalid, CancellationToken.None));
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(historyCount, fixture.Store.ReadAll<JsonElement>("configuration-history").Count);
        Assert.Equal(InputA, (await fixture.Supervisor.ConfigurationAsync()).Channels.Single(channel => channel.Id == "ch1").DeviceName);
        var otherOnly = before with { Channels = before.Channels.Select(channel => channel.Id == "ch2" ? channel with { DeviceName = InputC } : channel).ToList() };
        await fixture.Supervisor.UpdateConfigurationAsync(otherOnly, CancellationToken.None);
        Assert.Equal(InputC, (await fixture.Supervisor.ConfigurationAsync()).Channels.Single(channel => channel.Id == "ch2").DeviceName);
        var retained = fixture.Store.ReadAll<RecordingJob>("recording").Single(record => record.Id == job.Id);
        Assert.Equal(InputA, retained.Channel.DeviceName);
        Assert.Equal(InputA, retained.Configuration.Channels.Single(channel => channel.Id == "ch1").DeviceName);
        Assert.Equal(recording ? JobState.Recording : JobState.Prepared, retained.State);
        Assert.Equal(1, fixture.Backend.OpenCalls);
    }
}
