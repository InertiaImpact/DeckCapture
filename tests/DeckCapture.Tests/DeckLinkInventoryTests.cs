using System.Text.Json;
using DeckCapture.Capture;

namespace DeckCapture.Tests;

public sealed class DeckLinkInventoryTests
{
    private static string Entry(string handle = "exact-key", string model = "DeckLink Duo 2", string profile = "2dhd", int index = 0,
        string group = "0x00000000fedcba98", long? selected = 1) => JsonSerializer.Serialize(new
        { deviceHandle = handle, modelName = model, displayName = "Custom [Deck name]", deviceGroupId = group,
          persistentId = "0x00000000ffffffff", subDeviceIndex = index, subDeviceCount = 4, profileId = profile,
          videoInputConnections = 3, selectedVideoInputConnection = selected });
    private static DeckLinkInventorySnapshot Snapshot(params string[] entries) =>
        DeckLinkInventory.Parse("{\"available\":true,\"devices\":[" + string.Join(',', entries) + "]}");

    [Theory]
    [InlineData("2dhd", 0, "sdi-1")]
    [InlineData("2dhd", 1, "sdi-3")]
    [InlineData("2dhd", 2, "sdi-2")]
    [InlineData("2dhd", 3, "sdi-4")]
    [InlineData("1dfd", 0, "sdi-1")]
    [InlineData("1dfd", 1, "sdi-3")]
    [InlineData("1dfd", 2, null)]
    [InlineData("1dfd", 3, null)]
    [InlineData("4dhd", 0, null)]
    [InlineData("2dfd", 0, null)]
    [InlineData("unknown", 0, null)]
    public void Duo2UsesItsDocumentedPairProfilesNotEightKProfiles(string profile, int index, string? connector)
        => Assert.Equal(connector, Assert.Single(Snapshot(Entry(profile: profile, index: index)).Devices).PhysicalConnectorId);

    [Theory]
    [InlineData("DeckLink Duo", "2dhd", 1)]
    [InlineData("DeckLink Duo 2", "2dhd", 2)]
    [InlineData("DeckLink Duo 2", "2dhd", null)]
    [InlineData("DeckLink Duo 2 Mini", "2dhd", 1)]
    public void UnknownModelOrSelectedNonSdiIsNeverInvented(string model, string profile, int? selected)
        => Assert.Null(Assert.Single(Snapshot(Entry(model: model, profile: profile, selected: selected)).Devices).PhysicalConnectorId);

    [Fact]
    public void JoinsExactHandlesAndGroupsOnlySdkIdsWhilePreservingDescriptions()
    {
        var discovery = new InputDiscoveryResult(true, true,
            [new("exact-key", "FFmpeg label"), new("second"), new("Custom [Deck name]"), new("unmatched")]);
        var result = DeckLinkInventory.Enrich(discovery, Snapshot(Entry(), Entry("second", index: 1), Entry("unused", group: "0x0000000000000000")));
        Assert.Equal("FFmpeg label", result.Devices[0].Description);
        Assert.Equal("0x00000000ffffffff", result.Devices[0].Inventory!.PersistentId);
        Assert.Equal(new[] { "SDI", "HDMI" }, result.Devices[0].Inventory!.SupportedVideoConnections);
        Assert.Null(result.Devices[2].Inventory); // display-name equality is never a join.
        Assert.Null(result.Devices[3].Inventory);
        var card = Assert.Single(result.Cards);
        Assert.Equal("0x00000000fedcba98", card.Id);
        Assert.Equal(new[] { "sdi-4", "sdi-3", "sdi-2", "sdi-1", "reference" }, card.ConnectorLayout.Select(c => c.Id));
        Assert.NotNull(result.InventoryError);
    }

    [Fact]
    public void SameModelDoesNotMergeDifferentCardsOrUnknownGroupIds()
    {
        var result = DeckLinkInventory.Enrich(new(true, true, [new("a"), new("b"), new("c")]),
            Snapshot(Entry("a"), Entry("b", group: "0x0000000000000002"), Entry("c", group: "0x0000000000000000")));
        Assert.Equal(2, result.Cards.Length);
        Assert.Null(result.Devices[2].Inventory!.DeviceGroupId);
    }

    [Fact]
    public void DuplicateSdkHandlesAreAmbiguousAndNotJoined() => Assert.Empty(Snapshot(Entry(), Entry(index: 1)).Devices);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"available\":true}")]
    [InlineData("{\"available\":true,\"devices\":null}")]
    [InlineData("{\"available\":true,\"devices\":{}}")]
    [InlineData("{\"available\":\"yes\",\"devices\":[]}")]
    [InlineData("{\"available\":true,\"devices\":[{\"deviceHandle\":3}]}")]
    public async Task OptionalMalformedInventoryPreservesSuccessfulFfmpegDiscovery(string json)
    {
        var discovery = new InputDiscovery((_, args, _) => Task.FromResult(new DiscoveryProcessResult(0,
            args.Contains("-devices") ? " D decklink Blackmagic input" : "Auto-detected sources for decklink:\n exact-key [Real description] (video)\n", "")),
            _ => Task.FromResult(DeckLinkInventory.Parse(json)));
        var result = await discovery.DiscoverAsync(new(), CancellationToken.None);
        Assert.True(result.Available);
        Assert.True(result.DeckLinkSupported);
        Assert.Equal("Real description", Assert.Single(result.Devices).Description);
        Assert.Empty(result.Cards);
        Assert.NotNull(result.InventoryError);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoInventoryWarning()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = new InputDiscovery((_, args, _) => Task.FromResult(new DiscoveryProcessResult(0,
            args.Contains("-devices") ? " D decklink Blackmagic input" : "Auto-detected sources for decklink:\n exact-key [Label] (video)\n", "")),
            token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Snapshot()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync(new(), cancellation.Token));
    }

    [Fact]
    public void MissingDriverIsExplicitAndHasNoDevices()
    {
        var result = DeckLinkInventory.Parse("{\"available\":false,\"devices\":[],\"error\":\"Driver absent\"}");
        Assert.False(result.Available);
        Assert.Empty(result.Devices);
        Assert.Equal("Driver absent", result.Error);
    }
}
