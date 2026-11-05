using System.Text.Json;
using System.Text.Json.Nodes;
using DeckCapture.Capture;

namespace DeckCapture.Tests;

public sealed class DeckLinkPciCorrelationTests
{
    private const string Pnp = @"PCI\VEN_BDBD&DEV_A11B\exact-instance";
    private static string Id(uint value) => "0x" + value.ToString("x16");
    private static JsonObject Sdk(string handle = "exact", uint topology = 5, uint persistent = 0) => new()
    { ["deviceHandle"] = handle, ["modelName"] = "DeckLink SDI", ["subDeviceIndex"] = 0, ["subDeviceCount"] = 2,
      ["topologicalId"] = Id(topology), ["persistentId"] = Id(persistent) };
    private static JsonObject Filter(uint topology = 5, uint persistent = 0, string? pnp = Pnp) => new()
    { ["friendlyName"] = "Friendly label does not identify a card", ["devicePath"] = @"\\?\pci#exact-registered-interface",
      ["pnpInstanceId"] = pnp, ["topologicalId"] = Id(topology), ["persistentId"] = Id(persistent), ["error"] = null };
    private static JsonObject Report(JsonObject[]? sdk = null, JsonObject[]? filters = null) => new()
    {
        ["available"] = true, ["devices"] = new JsonArray((sdk ?? [Sdk()]).Cast<JsonNode>().ToArray()), ["sdkTruncated"] = false,
        ["pciInventory"] = new JsonObject { ["available"] = true, ["devices"] = new JsonArray(), ["truncated"] = false },
        ["directShowCorrelation"] = new JsonObject { ["available"] = true,
            ["filters"] = new JsonArray((filters ?? [Filter()]).Cast<JsonNode>().ToArray()), ["truncated"] = false }
    };
    private static DeckLinkPciCorrelationResult Parse(JsonObject json) => DeckLinkPciCorrelation.Parse(json.ToJsonString());

    [Fact]
    public void ExactUniqueTopologyJoinsOnlyReportedIdsAndReturnsSameObservationMetadata()
    {
        var result = Parse(Report());
        Assert.True(result.Available); Assert.Null(result.Error);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("ExactIdMatch", endpoint.Status); Assert.Equal("TopologicalId", endpoint.MatchMethod);
        Assert.Equal(Pnp, endpoint.PnpInstanceId); Assert.Equal("exact", endpoint.DeviceHandle);
        Assert.Equal(Id(5), Assert.Single(result.SdkDevices).TopologicalId);
        Assert.Equal(2, result.SdkDevices[0].SubDeviceCount); Assert.Equal("DeckLink SDI", result.SdkDevices[0].ModelName);
        Assert.True(result.PciInventory!.Available); Assert.Equal(Pnp, Assert.Single(result.Filters).PnpInstanceId);
        Assert.Null(result.SdkDevices[0].DeviceGroupId); Assert.Null(result.SdkDevices[0].PhysicalConnectorId);
    }

    [Fact]
    public void PrefersUniquePersistentIdButCrossChecksKnownTopology()
    {
        var endpoint = Assert.Single(Parse(Report([Sdk(persistent: 9)], [Filter(persistent: 9)])).Endpoints);
        Assert.Equal("ExactIdMatch", endpoint.Status); Assert.Equal("PersistentId", endpoint.MatchMethod);
        var conflict = Assert.Single(Parse(Report([Sdk(persistent: 9)], [Filter(topology: 6, persistent: 9)])).Endpoints);
        Assert.Equal("Conflict", conflict.Status); Assert.Null(conflict.PnpInstanceId);
        Assert.Equal("Conflict", Assert.Single(Parse(Report([Sdk(persistent: 9)], [Filter(persistent: 10)])).Endpoints).Status);
    }

    [Fact]
    public void DuplicateObservedIdsAcrossEitherApiAreAmbiguous()
    {
        var duplicateFilters = Parse(Report(filters: [Filter(), Filter()]));
        Assert.Equal("Ambiguous", Assert.Single(duplicateFilters.Endpoints).Status);
        Assert.Null(duplicateFilters.Endpoints[0].PnpInstanceId);
        var duplicateSdk = Parse(Report([Sdk("first"), Sdk("second")], [Filter()]));
        Assert.All(duplicateSdk.Endpoints, endpoint => { Assert.Equal("Ambiguous", endpoint.Status); Assert.Null(endpoint.PnpInstanceId); });
        var conflictingApis = Parse(Report([Sdk(persistent: 9)], [Filter(persistent: 10), Filter(topology: 6, persistent: 9)]));
        Assert.Equal("Conflict", Assert.Single(conflictingApis.Endpoints).Status);
    }

    [Fact]
    public void ZeroIdsUnresolvedInterfacesAndMissingDevicePathsDoNotBecomeAssociations()
    {
        Assert.Equal("Unmatched", Assert.Single(Parse(Report([Sdk(topology: 0)], [Filter(topology: 0)])).Endpoints).Status);
        Assert.Equal("Unmatched", Assert.Single(Parse(Report(filters: [Filter(pnp: null)])).Endpoints).Status);
        Assert.Equal("Unmatched", Assert.Single(Parse(Report(filters: [Filter(pnp: @"USB\VEN_BDBD&DEV_A11B")])).Endpoints).Status);
        var filter = Filter(); filter["devicePath"] = null;
        Assert.Equal("Unmatched", Assert.Single(Parse(Report(filters: [filter])).Endpoints).Status);
        var result = Parse(Report([Sdk("a")], [Filter(topology: 6)]));
        Assert.Equal("Unmatched", Assert.Single(result.Endpoints).Status); // No enum-order or friendly-label fallback.
    }

    [Fact]
    public void DescendantInterfaceKeepsTheExactFirstPciAncestorEvidence()
    {
        var filter = Filter(); filter["interfaceInstanceId"] = @"DECKLINK\AVSTREAM\capture";
        filter["interfaceAncestorInstanceIds"] = new JsonArray(@"DECKLINK\BUS\child", Pnp);
        filter["interfaceAncestryTruncated"] = false;
        var result = Parse(Report(filters: [filter]));
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal("ExactIdMatch", endpoint.Status); Assert.Equal(Pnp, endpoint.PnpInstanceId);
        var raw = Assert.Single(result.Filters);
        Assert.Equal(@"DECKLINK\AVSTREAM\capture", raw.InterfaceInstanceId);
        Assert.Equal(new[] { @"DECKLINK\BUS\child", Pnp }, raw.InterfaceAncestorInstanceIds);
    }

    [Fact]
    public void ForeignFirstPciAncestorAndTruncatedOrCyclicChainCannotBeAccepted()
    {
        var filter = Filter(); filter["interfaceInstanceId"] = @"DECKLINK\AVSTREAM\capture";
        filter["interfaceAncestorInstanceIds"] = new JsonArray(@"PCI\VEN_8086&DEV_AAAA\foreign", Pnp);
        Assert.Equal("Unmatched", Assert.Single(Parse(Report(filters: [filter])).Endpoints).Status);
        filter = Filter(); filter["interfaceInstanceId"] = @"DECKLINK\AVSTREAM\capture";
        filter["interfaceAncestorInstanceIds"] = new JsonArray(Pnp); filter["interfaceAncestryTruncated"] = true;
        Assert.Equal("Unmatched", Assert.Single(Parse(Report(filters: [filter])).Endpoints).Status);
        filter = Filter(); filter["interfaceInstanceId"] = @"DECKLINK\AVSTREAM\capture";
        filter["interfaceAncestorInstanceIds"] = new JsonArray(@"DECKLINK\AVSTREAM\capture");
        Assert.Throws<InvalidDataException>(() => Parse(Report(filters: [filter])));
    }

    [Theory]
    [InlineData("sdkTruncated")]
    [InlineData("filterTruncated")]
    [InlineData("pciTruncated")]
    public void TruncatedEvidenceCannotEstablishUniqueIdentity(string section)
    {
        var root = Report();
        if (section == "sdkTruncated") root["sdkTruncated"] = true;
        else root[section == "filterTruncated" ? "directShowCorrelation" : "pciInventory"]!["truncated"] = true;
        var result = Parse(root);
        Assert.True(result.Truncated); Assert.NotNull(result.Error);
        var endpoint = Assert.Single(result.Endpoints); Assert.Equal("Ambiguous", endpoint.Status); Assert.Null(endpoint.PnpInstanceId);
    }

    [Fact]
    public void MissingOrDuplicateSdkHandlesDoNotMakeRemainingIdsFalselyUnique()
    {
        var result = Parse(Report([Sdk("good", 9), Sdk("duplicate"), Sdk("duplicate")], [Filter(9)]));
        Assert.Equal("Ambiguous", Assert.Single(result.Endpoints).Status);
        Assert.Null(result.Endpoints[0].PnpInstanceId); Assert.NotNull(result.Error);
    }

    [Fact]
    public void MissingHelperModeOrUnavailableApisRemainExplicit()
    {
        var oldHelper = Report(); oldHelper.Remove("directShowCorrelation");
        var old = Parse(oldHelper); Assert.False(old.Available); Assert.NotNull(old.Error); Assert.Single(old.SdkDevices);
        var failed = Report(); failed["directShowCorrelation"]!["available"] = false;
        failed["directShowCorrelation"]!["error"] = "Cannot enumerate video filters";
        var result = Parse(failed); Assert.False(result.Available); Assert.Equal("Cannot enumerate video filters", result.Error);
        Assert.Null(Assert.Single(result.Endpoints).PnpInstanceId);
    }

    [Fact]
    public async Task FixedFlagHasNoCallerArgumentsAndMalformedHelperFailsWithoutPartialSuccess()
    {
        using var fixture = new HelperFile();
        var result = await DeckLinkPciCorrelation.ProbeAsync((path, arguments, _) =>
        {
            Assert.Equal(fixture.Path, path); Assert.Equal(new[] { "--pcie-correlate-idle" }, arguments);
            return Task.FromResult(new DiscoveryProcessResult(0, Report().ToJsonString(), ""));
        }, fixture.Path, CancellationToken.None);
        Assert.True(result.Available);
        foreach (var json in new[] { "{}", "not json", "{\"available\":true,\"devices\":[],\"directShowCorrelation\":null}" })
        {
            var malformed = await DeckLinkPciCorrelation.ProbeAsync((_, _, _) => Task.FromResult(new DiscoveryProcessResult(0, json, "")), fixture.Path, CancellationToken.None);
            Assert.False(malformed.Available); Assert.Empty(malformed.Endpoints); Assert.NotNull(malformed.Error);
        }
        Assert.False((await DeckLinkPciCorrelation.ProbeAsync((_, _, _) => throw new InvalidOperationException("Must not run"), null, CancellationToken.None)).Available);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationReleaseTheBoundedGate()
    {
        using var fixture = new HelperFile();
        var expired = await DeckLinkPciCorrelation.ProbeAsync(async (_, _, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(0, "", ""); }, fixture.Path, CancellationToken.None, TimeSpan.FromMilliseconds(20));
        Assert.False(expired.Available); Assert.Contains("deadline", expired.Error);
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = DeckLinkPciCorrelation.ProbeAsync(async (_, _, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(0, "", ""); }, fixture.Path, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondCalls = 0;
        var waiting = await DeckLinkPciCorrelation.ProbeAsync((_, _, _) => { secondCalls++; return Task.FromResult(new DiscoveryProcessResult(0, "", "")); },
            fixture.Path, CancellationToken.None, TimeSpan.FromMilliseconds(20));
        Assert.False(waiting.Available); Assert.Equal(0, secondCalls);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        var retry = await DeckLinkPciCorrelation.ProbeAsync((_, _, _) => Task.FromResult(new DiscoveryProcessResult(0, Report().ToJsonString(), "")), fixture.Path, CancellationToken.None);
        Assert.True(retry.Available);
    }

    private sealed class HelperFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deckcapture-helper-" + Guid.NewGuid().ToString("N") + ".exe");
        public HelperFile() => File.WriteAllText(Path, "Explicit fake runner never executes this file.");
        public void Dispose() => File.Delete(Path);
    }
}
