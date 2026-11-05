using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class DiscoveryTests
{
    private const string Capabilities = "Devices:\n D  decklink         Blackmagic DeckLink input\n";
    private const string Sources = "Auto-detected sources for decklink:\n  device-handle-1 [DeckLink Duo [BNC 1]] (video, audio)\n";
    private const string Formats = "[decklink @ 00001] Supported formats for 'device-handle-1':\n\tformat_code\tdescription\n\tntsc\t\t720x486 at 30000/1001 fps (interlaced, lower field first)\n\tpal \t\t720x576 at 25/1 fps (interlaced, upper field first)\n\tHi50\t\t1920x1080 at 25/1 fps (interlaced, upper field first)\n";

    [Fact]
    public void ParsesActualSourceGrammarWithoutInventingConfiguredDevices()
    {
        var text = Sources + "* device-handle-2 [DeckLink O'Brien Camera] (none)\n" +
            "  device-handle-1 [Duplicate display] (video)\n  Device [Name] [Description [nested]] (audio, video)\n" +
            "Cannot list sources: no cards\nAuto-detected sources for dshow:\n  webcam [Not DeckLink] (video)\n";
        Assert.Equal(new[] { "device-handle-1", "device-handle-2", "Device [Name]" }, InputDiscovery.ParseSources(text).Select(device => device.Name));
        Assert.Empty(InputDiscovery.ParseSources("  fake-channel [Configured Deck 1] (video)\n"));
    }

    [Fact]
    public void ParsesFourCcIncludingPalPaddingAndPreservesModeLabels()
    {
        var modes = InputDiscovery.ParseFormats(Formats);
        Assert.Equal(new[] { "ntsc", "pal", "Hi50" }, modes.Select(mode => mode.Code));
        Assert.Contains("30000/1001 fps", modes[0].Label);
        Assert.Contains("lower field first", modes[0].Label);
        Assert.Empty(InputDiscovery.ParseFormats("\tHi50\t1920x1080 at 25/1 fps\n"));
    }

    [Fact]
    public async Task UnsupportedBuildDoesNotTreatSilentSuccessfulSourcesCommandAsHardware()
    {
        var calls = 0;
        var discovery = new InputDiscovery((executable, arguments, token) =>
        {
            calls++;
            Assert.Contains("-devices", arguments);
            return Task.FromResult(new DiscoveryProcessResult(0, " D lavfi Synthetic sources\n", ""));
        });
        var result = await discovery.DiscoverAsync(new(), CancellationToken.None);
        Assert.False(result.Available);
        Assert.False(result.DeckLinkSupported);
        Assert.Empty(result.Devices);
        Assert.Contains("does not include DeckLink", result.Error);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Auto-detected sources for decklink:\n", true)]
    [InlineData("Auto-detected sources for decklink:\nCannot list sources: Input/output error\n", false)]
    [InlineData("The DeckLink drivers could not be found.\n", false)]
    public async Task DistinguishesCompiledSupportFromEmptyOrFailedHardwareEnumeration(string output, bool available)
    {
        var discovery = new InputDiscovery((executable, arguments, token) => Task.FromResult(arguments.Contains("-devices")
            ? new DiscoveryProcessResult(0, Capabilities, "") : new DiscoveryProcessResult(0, "", output)));
        var result = await discovery.DiscoverAsync(new(), CancellationToken.None);
        Assert.Equal(available, result.Available);
        Assert.True(result.DeckLinkSupported);
        Assert.Empty(result.Devices);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task FormatListingAcceptsExpectedNonzeroExitAndPassesExactDeviceArgument()
    {
        const string deviceName = "Device O'Brien [A] -i another-input";
        var source = $"Auto-detected sources for decklink:\n  {deviceName} [Display name] (video)\n";
        string[]? formatArguments = null;
        var discovery = new InputDiscovery((executable, arguments, token) =>
        {
            if (arguments.Contains("-devices")) return Task.FromResult(new DiscoveryProcessResult(0, Capabilities, ""));
            if (arguments.Contains("-sources")) return Task.FromResult(new DiscoveryProcessResult(0, source, ""));
            formatArguments = arguments.ToArray();
            return Task.FromResult(new DiscoveryProcessResult(1, "", Formats));
        });
        var result = await discovery.DiscoverFormatsAsync(new(), deviceName, CancellationToken.None);
        Assert.True(result.Available, result.Error);
        Assert.Equal(3, result.Modes.Length);
        Assert.Equal(deviceName, formatArguments![^1]);
        Assert.Equal("-i", formatArguments[^2]);
        Assert.Equal(10, formatArguments.Length);
    }

    [Fact]
    public async Task UnknownDeviceNeverOpensAnInputAndCallerCancellationPropagates()
    {
        var formatCalls = 0;
        var discovery = new InputDiscovery((executable, arguments, token) =>
        {
            if (arguments.Contains("-list_formats")) formatCalls++;
            return Task.FromResult(new DiscoveryProcessResult(0, arguments.Contains("-devices") ? Capabilities : Sources, ""));
        });
        var unknown = await discovery.DiscoverFormatsAsync(new(), "not-currently-discovered", CancellationToken.None);
        Assert.False(unknown.Available);
        Assert.Equal(0, formatCalls);
        Assert.Throws<ArgumentException>(() => InputDiscovery.ValidateDeviceName("device\n-i other"));
        using var canceled = new CancellationTokenSource();
        var blocked = new InputDiscovery(async (executable, arguments, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(0, "", "");
        });
        var task = blocked.DiscoverAsync(new(), canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task MissingExecutableAndOversizedInventoryFailWithoutPartialSuccess()
    {
        var profile = new CaptureProfile { FfmpegPath = Path.Combine(Path.GetTempPath(), "missing-discovery-" + Guid.NewGuid().ToString("N"), "ffmpeg.exe") };
        var result = await new InputDiscovery().DiscoverAsync(profile, CancellationToken.None);
        Assert.False(result.Available);
        Assert.False(result.DeckLinkSupported);
        Assert.Empty(result.Devices);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        var tooMany = "Auto-detected sources for decklink:\n" + string.Join('\n', Enumerable.Range(0, 257).Select(index => $"  device-{index} [DeckLink] (video)"));
        Assert.Throws<InvalidDataException>(() => InputDiscovery.ParseSources(tooMany));
    }

    [Fact]
    public async Task FormatReservationBlocksNewCaptureWithoutHoldingTheSupervisorStateGate()
    {
        await using var fixture = new DiscoveryFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Supervisor.InputDiscoveryService = new InputDiscovery(async (executable, arguments, token) =>
        {
            if (arguments.Contains("-devices")) return new(0, Capabilities, "");
            if (arguments.Contains("-sources")) return new(0, Sources, "");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(1, "", Formats);
        });
        var formatTask = fixture.Supervisor.DiscoverInputFormatsAsync("device-handle-1", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await fixture.Supervisor.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var conflict = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request, CancellationToken.None));
            Assert.Equal(409, conflict.StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.True((await formatTask).Available);
        var prepared = await fixture.Supervisor.PrepareAsync(fixture.Request, CancellationToken.None);
        Assert.Equal(JobState.Prepared, prepared.State);
        var activeConflict = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.DiscoverInputFormatsAsync("device-handle-1", CancellationToken.None));
        Assert.Equal(409, activeConflict.StatusCode);
    }

    [Fact]
    public async Task PciProbeReservesInputsAndMaintenanceWithoutBlockingStateReads()
    {
        await using var fixture = new DiscoveryFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Supervisor.PciIdentityProbeAsync = async token =>
        {
            calls++;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new DeckLinkPciCorrelationResult(true);
        };
        var probe = fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await fixture.Supervisor.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request, CancellationToken.None))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.DiscoverInputFormatsAsync("device-handle-1", CancellationToken.None))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None))).StatusCode);
        }
        finally { release.TrySetResult(); }
        await probe;
        await fixture.Supervisor.PrepareAsync(fixture.Request, CancellationToken.None);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None))).StatusCode);
        Assert.Equal(1, calls); // Rejected commands must never bind a filter.
    }

    [Fact]
    public async Task PciProbeFailureAndCancellationReleaseTheReservation()
    {
        await using var fixture = new DiscoveryFixture();
        fixture.Supervisor.PciIdentityProbeAsync = _ => throw new IOException("Fixture probe failed.");
        await Assert.ThrowsAsync<IOException>(() => fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None));
        await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
        await fixture.Supervisor.SetMaintenanceAsync(false, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Supervisor.PciIdentityProbeAsync = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new DeckLinkPciCorrelationResult(false);
        };
        var probe = fixture.Supervisor.ProbePcieIdentityAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.Equal(JobState.Prepared, (await fixture.Supervisor.PrepareAsync(fixture.Request, CancellationToken.None)).State);
    }

    [Fact]
    public async Task MaintenanceRejectsPciProbeBeforeAnyFilterBinding()
    {
        await using var fixture = new DiscoveryFixture();
        fixture.Supervisor.PciIdentityProbeAsync = _ => throw new InvalidOperationException("The idle guard must reject before invoking this.");
        await fixture.Supervisor.SetMaintenanceAsync(true, CancellationToken.None);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.ProbePcieIdentityAsync(CancellationToken.None))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.DiscoverInputFormatsAsync("device-handle-1", CancellationToken.None))).StatusCode);
    }
    [CaptureIntegrationFact]
    public async Task InstalledFfmpegDiscoveryReportsItsActualCapabilitiesWithoutOpeningAFormat()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var profile = new CaptureProfile();
        var capabilities = await new CaptureBackend().ProbeAsync(profile, timeout.Token);
        Assert.True(capabilities.Available, capabilities.Error);
        var result = await new InputDiscovery().DiscoverAsync(profile, timeout.Token);
        Assert.Equal(capabilities.DeckLinkSupported, result.DeckLinkSupported);
        if (!result.DeckLinkSupported) { Assert.False(result.Available); Assert.Empty(result.Devices); Assert.Contains("DeckLink", result.Error); }
    }

    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "deckcapture-discovery-" + Guid.NewGuid().ToString("N"));
        public CaptureSupervisor Supervisor { get; }
        public PrepareRecordingRequest Request { get; } = new("ch1", "Discovery test", 5, "local-test", "tests");
        public DiscoveryFixture()
        {
            var store = new StateStore(directory);
            store.Save("configuration", "current", ConfigurationRules.DevelopmentDefaults(directory) with { EmergencyReserveBytes = 0, Capture = new() { ReservationBytesPerSecond = 100_000 } });
            var secretStore = new SecretStore(new EphemeralDataProtectionProvider(), directory);
            Supervisor = new(store, new NoDeviceBackend(), new NoDelivery(), new NotificationService(store, secretStore, NullLogger<NotificationService>.Instance), NullLogger<CaptureSupervisor>.Instance, directory);
        }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None);
            Supervisor.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
    private sealed class NoDeviceBackend : ICaptureBackend
    {
        public Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken) => Task.FromResult(new CaptureCapabilities(true, false, "Explicit discovery test fake"));
        public Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken) => Task.FromResult<ICaptureSession>(new NoDeviceSession());
    }
    private sealed class NoDeviceSession : ICaptureSession
    {
        private readonly TaskCompletionSource<CaptureResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureMetrics Metrics => new() { SignalLocked = true };
        public Task<CaptureResult> Completion => completion.Task;
        public Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken) => throw new InvalidOperationException("This test must not record.");
        public Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<CaptureResult> StopAsync(CancellationToken cancellationToken) => Task.FromResult(new CaptureResult(false, null, 0, 0, 0, "Explicit unit test monitor"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoDelivery : IFileDelivery
    {
        public Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates, IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("This test must not deliver.");
    }
}
