using System.Text.Json;
using System.Text.Json.Nodes;
using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

public sealed class InputWiringTests
{
    private static PhysicalInputWiring Card(string id = "duo-a", string endpoint = "opaque:exact:key", string connector = "sdi-1-in") => new()
    { Id = id, Label = "Archive rack / left card", ModelId = "decklink-duo-original", Inputs = [new(endpoint, connector)] };
    private static AppConfiguration Configuration(string directory) => ConfigurationRules.DevelopmentDefaults(directory) with
    { EmergencyReserveBytes = 0, Capture = new() { ReservationBytesPerSecond = 100_000 }, InputWiring = [Card()] };

    [Fact]
    public void LegacyCatalogHasSeparateFixedInputOutputPairsAndBottomReference()
    {
        var duo = DeckLinkModelCatalog.Find("decklink-duo-original")!;
        Assert.Equal(new[] { "sdi-2-out", "sdi-2-in", "sdi-1-out", "sdi-1-in", "reference" }, duo.Connectors.Select(port => port.Id));
        Assert.Equal(new[] { "Output", "Input", "Output", "Input", "Input" }, duo.Connectors.Select(port => port.Direction));
        Assert.Equal("57", duo.SourcePage);
        Assert.True(duo.VendorVerified);
        var quad = DeckLinkModelCatalog.Find("decklink-quad-original")!;
        Assert.Equal(9, quad.Connectors.Length);
        Assert.Equal("sdi-4-out", quad.Connectors[0].Id);
        Assert.Equal("reference", quad.Connectors[^1].Id);
        Assert.False(DeckLinkModelCatalog.Find("custom")!.VendorVerified);
        Assert.Null(DeckLinkModelCatalog.FindSdkModel("DeckLink SDI")); // Not proof of an original Duo.
    }

    [Fact]
    public void CatalogReturnsIndependentArraysAndKeepsUnknownModelsUnknown()
    {
        var first = DeckLinkModelCatalog.Models;
        first[0].Connectors[0] = new("fake", "Fake", "Other");
        Assert.Equal("sdi-2-out", DeckLinkModelCatalog.Models[0].Connectors[0].Id);
        Assert.Null(DeckLinkModelCatalog.FindSdkModel("DeckLink 8K Pro G2"));
        Assert.Null(DeckLinkModelCatalog.Find("does-not-exist"));
    }

    [Theory]
    [InlineData("DeckLink Quad 2", "2dhd", 0, "sdi-1")]
    [InlineData("DeckLink Quad 2", "2dhd", 1, "sdi-3")]
    [InlineData("DeckLink Quad 2", "2dhd", 2, "sdi-5")]
    [InlineData("DeckLink Quad 2", "2dhd", 3, "sdi-7")]
    [InlineData("DeckLink Quad 2", "2dhd", 4, "sdi-2")]
    [InlineData("DeckLink Quad 2", "2dhd", 5, "sdi-4")]
    [InlineData("DeckLink Quad 2", "2dhd", 6, "sdi-6")]
    [InlineData("DeckLink Quad 2", "2dhd", 7, "sdi-8")]
    [InlineData("DeckLink Quad 2", "1dfd", 0, "sdi-1")]
    [InlineData("DeckLink Quad 2", "1dfd", 1, "sdi-3")]
    [InlineData("DeckLink Quad 2", "1dfd", 2, "sdi-5")]
    [InlineData("DeckLink Quad 2", "1dfd", 3, "sdi-7")]
    [InlineData("DeckLink Quad 2", "1dfd", 4, null)]
    [InlineData("DeckLink Quad 2", "4dhd", 0, null)]
    [InlineData("DeckLink 8K Pro", "4dhd", 0, "sdi-1")]
    [InlineData("DeckLink 8K Pro", "4dhd", 1, "sdi-3")]
    [InlineData("DeckLink 8K Pro", "4dhd", 2, "sdi-2")]
    [InlineData("DeckLink 8K Pro", "4dhd", 3, "sdi-4")]
    [InlineData("DeckLink 8K Pro", "2dfd", 0, "sdi-1")]
    [InlineData("DeckLink 8K Pro", "2dfd", 1, "sdi-3")]
    [InlineData("DeckLink 8K Pro", "2dfd", 2, null)]
    [InlineData("DeckLink SDI", "2dhd", 0, null)]
    public void ProfileMappingsAreExactAndInactiveSubdevicesStayUnmapped(string model, string profile, int index, string? expected)
    {
        var ports = DeckLinkModelCatalog.SdkInputConnectors(model, profile, index, 8);
        if (expected is null) Assert.Empty(ports); else Assert.Equal(expected, Assert.Single(ports));
    }

    [Theory]
    [InlineData("1dfd", 2)]
    [InlineData("1dhd", 4)]
    public void EightKMultiBncProfilesRetainEveryAssignedInputWithoutInventingOne(string profile, int expected)
    {
        var metadata = new DeckLinkInputInventory("exact", "0x0000000000001234", null, "DeckLink 8K Pro", "Custom label", 0, 4, profile, ["SDI"], "SDI", null);
        var result = DeckLinkInventory.Enrich(new(true, true, [new("exact")]), new(true, [metadata]));
        Assert.Equal(expected, Assert.Single(result.Devices).Connection!.ConnectorIds.Length);
        Assert.Null(result.Devices[0].Inventory!.PhysicalConnectorId);
        Assert.Equal(expected, Assert.Single(result.Cards).Inputs.Length);
        Assert.Equal("decklink-8k-pro", result.Cards[0].ModelId);
    }

    [Fact]
    public void OperatorMapsOnlyExactEndpointAndDoesNotRewriteOriginalEvidenceOrCaptureKey()
    {
        const string exact = "Deck O'Brien [A] -i another source";
        var metadata = new DeckLinkInputInventory(exact, null, null, "DeckLink SDI", "SDI (1)", 0, 1, null, ["SDI"], "SDI", null);
        var discovery = new InputDiscoveryResult(true, true, [new("nearby"), new(exact, "FFmpeg description", metadata)]);
        var result = InputWiring.Apply(discovery, [Card(endpoint: exact)]);
        Assert.Null(result.Devices[0].Connection);
        Assert.Equal(exact, result.Devices[1].Name);
        Assert.Equal(metadata, result.Devices[1].Inventory);
        Assert.Equal("Operator", result.Devices[1].Connection!.MappingSource);
        Assert.Equal("operator:duo-a", result.Devices[1].Connection!.CardId);
        var card = Assert.Single(result.Cards);
        Assert.Equal("Archive rack / left card", card.Label);
        Assert.True(Assert.Single(card.Inputs).Applied);
        Assert.True(card.Inputs[0].Present);
        Assert.Equal("DeckLink SDI", result.Devices[1].Inventory!.ModelName); // Operator model never replaces SDK model.
    }

    [Fact]
    public void AbsentAndReorderedEndpointsAreRetainedWithoutReassignmentEvenWithoutFfmpeg()
    {
        var card = Card();
        var unavailable = InputWiring.Apply(new(false, false, [], "FFmpeg unavailable"), [card]);
        Assert.False(unavailable.Available);
        Assert.False(Assert.Single(Assert.Single(unavailable.Cards).Inputs).Present);
        Assert.False(unavailable.Cards[0].Inputs[0].Applied);
        Assert.Equal("opaque:exact:key", unavailable.Cards[0].Inputs[0].DeviceName);
        var replaced = InputWiring.Apply(new(true, true, [new("different-handle")]), [card]);
        Assert.Null(Assert.Single(replaced.Devices).Connection);
        var reordered = InputWiring.Apply(new(true, true, [new("other"), new("opaque:exact:key")]), [card]);
        Assert.Null(reordered.Devices[0].Connection);
        Assert.Equal("sdi-1-in", Assert.Single(reordered.Devices[1].Connection!.ConnectorIds));
    }

    [Fact]
    public void SdkCardEvidenceWinsAndOperatorConflictRemainsVisible()
    {
        var metadata = new DeckLinkInputInventory("exact", "0x0000000000000001", null, "DeckLink Duo 2", "Duo2", 0, 4, "2dhd", ["SDI"], "SDI", "sdi-1");
        var discovered = DeckLinkInventory.Enrich(new(true, true, [new("exact")]), new(true, [metadata]));
        var result = InputWiring.Apply(discovered, [Card(endpoint: "exact")]);
        Assert.Equal("Sdk", Assert.Single(result.Devices).Connection!.MappingSource);
        Assert.Equal("0x0000000000000001", result.Devices[0].Connection!.CardId);
        var manual = Assert.Single(result.Cards, card => card.MappingSource == "Operator");
        Assert.True(Assert.Single(manual.Inputs).Present);
        Assert.False(manual.Inputs[0].Applied);
        Assert.Contains("conflicts", result.InventoryError);
    }

    private static DeckLinkInputInventory UnknownSdk(string handle, string? group, string? connector = null) =>
        new(handle, group, null, "Unlisted card model", "Endpoint", 0, 1, null, ["SDI"], "SDI", connector);

    [Fact]
    public void UnknownSdkLayoutCanBeAnnotatedWithoutChangingItsVerifiedGroupOrRawModel()
    {
        const string group = "0x0000000000001234";
        var metadata = UnknownSdk("exact", group);
        var discovered = DeckLinkInventory.Enrich(new(true, true, [new("exact")]), new(true, [metadata]));
        var result = InputWiring.Apply(discovered, [Card(endpoint: "exact")]);
        var card = Assert.Single(result.Cards);
        Assert.Equal(group, card.Id);
        Assert.Equal("Operator", card.MappingSource);
        Assert.Equal("decklink-duo-original", card.ModelId);
        Assert.Equal(metadata, Assert.Single(result.Devices).Inventory);
        Assert.Equal(group, result.Devices[0].Connection!.CardId);
        Assert.True(Assert.Single(card.Inputs).Applied);
    }

    [Fact]
    public void OperatorCardCannotMergeSdkGroupsOrSplitOneSdkGroupAcrossOperatorCards()
    {
        var a = UnknownSdk("a", "0x0000000000000001");
        var b = UnknownSdk("b", "0x0000000000000002");
        var discovered = DeckLinkInventory.Enrich(new(true, true, [new("a"), new("b")]), new(true, [a, b]));
        var merged = InputWiring.Apply(discovered, [Card() with { Inputs = [new("a", "sdi-1-in"), new("b", "sdi-2-in")] }]);
        Assert.All(merged.Devices, device => Assert.Null(device.Connection));
        Assert.All(Assert.Single(merged.Cards, card => card.MappingSource == "Operator").Inputs, binding => Assert.False(binding.Applied));
        var sameGroup = DeckLinkInventory.Enrich(new(true, true, [new("a"), new("b")]), new(true, [a, b with { DeviceGroupId = a.DeviceGroupId }]));
        var split = InputWiring.Apply(sameGroup, [Card("first", "a"), Card("second", "b")]);
        Assert.All(split.Devices, device => Assert.Null(device.Connection));
        Assert.All(split.Cards.Where(card => card.MappingSource == "Operator").SelectMany(card => card.Inputs), binding => Assert.False(binding.Applied));
    }

    [Fact]
    public void KnownSdkConnectorCannotBeOverriddenWhenGroupIdIsMissing()
    {
        var metadata = UnknownSdk("exact", null, "sdi-3");
        var result = InputWiring.Apply(new(true, true, [new("exact", Inventory: metadata)]), [Card(endpoint: "exact")]);
        Assert.Null(Assert.Single(result.Devices).Connection);
        Assert.Equal("sdi-3", result.Devices[0].Inventory!.PhysicalConnectorId);
        Assert.False(Assert.Single(Assert.Single(result.Cards).Inputs).Applied);
        Assert.Contains("conflicts", result.InventoryError);
    }

    [Theory]
    [InlineData("sdi-1-out")]
    [InlineData("reference")]
    [InlineData("absent")]
    public void ValidationRejectsNonCaptureOrUnknownConnectors(string connector)
    {
        var config = Configuration(Path.GetTempPath()) with { InputWiring = [Card(connector: connector)] };
        Assert.Contains(ConfigurationRules.Validate(config), error => error.Contains("Map endpoints only", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationRejectsDuplicateEndpointsSlotsNullEntriesAndUnboundedLayouts()
    {
        var config = Configuration(Path.GetTempPath());
        Assert.Empty(ConfigurationRules.Validate(config));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = [Card(), Card("other")] }));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = [Card() with { Inputs = [new("a", "sdi-1-in"), new("b", "sdi-1-in")] }] }));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = [null!] }));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = [Card() with { Inputs = null! }] }));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = Enumerable.Range(0, 33).Select(i => Card(i.ToString(), i.ToString())).ToList() }));
        Assert.NotEmpty(ConfigurationRules.Validate(config with { InputWiring = [Card() with { Id = "bad/id", Label = "bad\nlabel" }] }));
    }

    [Fact]
    public void CustomLayoutsCanRepresentOtherHardwareWithoutVendorClaim()
    {
        var custom = Card() with { ModelId = "custom", CustomConnectors = [new("camera-in", "Camera HDMI input", "HDMI", "Input")],
            Inputs = [new("opaque:exact:key", "camera-in")] };
        Assert.Empty(ConfigurationRules.Validate(Configuration(Path.GetTempPath()) with { InputWiring = [custom] }));
        var result = InputWiring.Apply(new(true, true, [new("opaque:exact:key")]), [custom]);
        var card = Assert.Single(result.Cards);
        Assert.Equal("custom", card.ModelId);
        Assert.Equal("Operator", card.MappingSource);
        Assert.Equal("HDMI", Assert.Single(card.ConnectorLayout).Kind);
        var tooMany = custom with { CustomConnectors = Enumerable.Range(0, 33).Select(i => new PhysicalInputConnector($"p{i}", "Input", "Other", "Input")).ToList() };
        Assert.NotEmpty(ConfigurationRules.Validate(Configuration(Path.GetTempPath()) with { InputWiring = [tooMany] }));
    }

    [Fact]
    public async Task ConfigExportImportAndSupervisorRestartPreserveOperatorMappingsWithoutHardware()
    {
        await using var fixture = new Fixture();
        var config = await fixture.Supervisor.ConfigurationAsync();
        var updated = await fixture.Supervisor.UpdateConfigurationAsync(config with { InputWiring = [Card()] }, CancellationToken.None);
        var exported = JsonSerializer.Serialize(updated, StateStore.Json);
        var imported = JsonSerializer.Deserialize<AppConfiguration>(exported, StateStore.Json)!;
        Assert.Empty(ConfigurationRules.Validate(imported));
        await fixture.Supervisor.UpdateConfigurationAsync(imported, CancellationToken.None);
        await fixture.RestartAsync();
        var restored = await fixture.Supervisor.ConfigurationAsync();
        Assert.Equal(exported, JsonSerializer.Serialize(restored, StateStore.Json));
        fixture.Supervisor.InputDiscoveryService = new InputDiscovery((_, _, _) => Task.FromResult(new DiscoveryProcessResult(0, " D lavfi Synthetic", "")));
        var discovery = await fixture.Supervisor.DiscoverInputsAsync(CancellationToken.None);
        Assert.False(discovery.Available);
        Assert.Equal("opaque:exact:key", Assert.Single(Assert.Single(discovery.Cards).Inputs).DeviceName);
        Assert.False(discovery.Cards[0].Inputs[0].Present);
        var oldJson = JsonNode.Parse(exported)!.AsObject(); oldJson.Remove("inputWiring");
        Assert.Empty(JsonSerializer.Deserialize<AppConfiguration>(oldJson.ToJsonString(), StateStore.Json)!.InputWiring);
    }

    [Fact]
    public async Task PreparedInputAnnotationCannotChangeButUnrelatedWiringCan()
    {
        await using var fixture = new Fixture();
        var initial = await fixture.Supervisor.ConfigurationAsync();
        var channel = initial.Channels[0] with { SourceKind = "DeckLink", DeviceName = "opaque:exact:key" };
        var config = initial with { Channels = [channel], InputWiring = [Card()] };
        await fixture.Supervisor.UpdateConfigurationAsync(config, CancellationToken.None);
        var prepared = await fixture.Supervisor.PrepareAsync(new(channel.Id, "Annotation test", 5, "local-test", "tests"), CancellationToken.None);
        var changed = config with { InputWiring = [Card() with { Label = "Different card annotation" }] };
        Assert.Equal(409, (await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.UpdateConfigurationAsync(changed, CancellationToken.None))).StatusCode);
        var unrelated = config with { InputWiring = [Card(), Card("other", "unrelated-endpoint")] };
        await fixture.Supervisor.UpdateConfigurationAsync(unrelated, CancellationToken.None);
        Assert.Equal("Archive rack / left card", Assert.Single(prepared.Configuration.InputWiring).Label);
        Assert.Equal("opaque:exact:key", prepared.Channel.DeviceName);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "deckcapture-wiring-" + Guid.NewGuid().ToString("N"));
        public CaptureSupervisor Supervisor { get; private set; }
        public Fixture()
        {
            var store = new StateStore(directory); store.Save("configuration", "current", Configuration(directory) with { InputWiring = [] });
            Supervisor = Create(store);
        }
        private CaptureSupervisor Create(StateStore store) => new(store, new NoDeviceBackend(), new NoDelivery(),
            new NotificationService(store, new SecretStore(new EphemeralDataProtectionProvider(), directory), NullLogger<NotificationService>.Instance),
            NullLogger<CaptureSupervisor>.Instance, directory);
        public async Task RestartAsync() { await Supervisor.StopAsync(CancellationToken.None); Supervisor.Dispose(); Supervisor = Create(new(directory)); }
        public async ValueTask DisposeAsync()
        {
            await Supervisor.StopAsync(CancellationToken.None); Supervisor.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }
    private sealed class NoDeviceBackend : ICaptureBackend
    {
        public Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken) => Task.FromResult(new CaptureCapabilities(true, true, "Unit test only"));
        public Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken) => Task.FromResult<ICaptureSession>(new NoDeviceSession());
    }
    private sealed class NoDeviceSession : ICaptureSession
    {
        private readonly TaskCompletionSource<CaptureResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureMetrics Metrics => new();
        public Task<CaptureResult> Completion => completion.Task;
        public Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<CaptureResult> StopAsync(CancellationToken cancellationToken) => Task.FromResult(new CaptureResult(false, null, 0, 0, 0, "Unit test monitor"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoDelivery : IFileDelivery
    {
        public Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates, IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
