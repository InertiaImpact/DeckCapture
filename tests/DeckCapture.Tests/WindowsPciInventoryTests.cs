using System.Text.Json;
using System.Text.Json.Nodes;
using DeckCapture.Capture;

namespace DeckCapture.Tests;

public sealed class WindowsPciInventoryTests
{
    private static JsonObject Node(string id = @"PCI\VEN_BDBD&DEV_AAAA\opaque", int bus = 46, int device = 12, int function = 0) => new()
    {
        ["instanceId"] = id, ["description"] = "Blackmagic card", ["friendlyName"] = null, ["manufacturer"] = "Blackmagic Design",
        ["service"] = "BlackmagicIO", ["hardwareIds"] = new JsonArray(@"PCI\VEN_BDBD&DEV_AAAA&REV_01"),
        ["busNumber"] = bus, ["address"] = (device << 16) | function, ["deviceNumber"] = device, ["functionNumber"] = function,
        ["locationInfo"] = "PCI bus 46, device 12, function 0", ["locationPaths"] = new JsonArray(@"PCIROOT(0)#PCI(0100)#PCI(0C00)"),
        ["uiNumber"] = 3, ["containerId"] = "{00000001-0002-0003-0004-000000000005}", ["physicalDeviceLocation"] = "0900000000000000",
        ["parents"] = new JsonArray(), ["parentsTruncated"] = false
    };
    private static JsonObject Root(params JsonObject[] nodes) => new()
    {
        ["available"] = true, ["devices"] = new JsonArray(), ["pciInventory"] = new JsonObject
        { ["available"] = true, ["devices"] = new JsonArray(nodes.Cast<JsonNode>().ToArray()), ["truncated"] = false, ["error"] = null }
    };
    private static DeckLinkInventorySnapshot Parse(JsonObject value) => DeckLinkInventory.Parse(value.ToJsonString());

    [Fact]
    public void PreservesExactRawSdkTopologyAndWindowsEvidenceWithoutCreatingAJoinOrGroup()
    {
        var pci = Node();
        var parent = Node(@"PCI\VEN_8086&DEV_AAAA\bridge", 0, 1, 0); parent.Remove("parents"); parent.Remove("parentsTruncated");
        pci["parents"] = new JsonArray(parent);
        var root = Root(pci);
        root["devices"] = new JsonArray(new JsonObject
        {
            ["deviceHandle"] = "revision:persistent:2e0c00", ["modelName"] = "DeckLink SDI",
            ["topologicalId"] = "0x00000000002e0c00", ["deviceInterface"] = 0x70636920,
            ["deviceGroupId"] = "0x0000000000000000", ["subDeviceIndex"] = 0, ["subDeviceCount"] = 1,
            ["videoInputConnections"] = 1, ["selectedVideoInputConnection"] = 1
        });
        var snapshot = Parse(root);
        var sdk = Assert.Single(snapshot.Devices);
        Assert.Equal("0x00000000002e0c00", sdk.TopologicalId);
        Assert.Equal(0x70636920, sdk.DeviceInterface);
        var windows = Assert.Single(snapshot.PciInventory!.Devices);
        Assert.Equal(46, windows.BusNumber); Assert.Equal(12, windows.DeviceNumber); Assert.Equal(0, windows.FunctionNumber);
        Assert.Equal(3, windows.UiNumber);
        Assert.Equal(@"PCI\VEN_8086&DEV_AAAA\bridge", Assert.Single(windows.Parents).InstanceId);
        Assert.Null(windows.FriendlyName);
        var discovery = DeckLinkInventory.Enrich(new(true, true, [new(sdk.DeviceHandle)]), snapshot);
        Assert.Same(snapshot.PciInventory, discovery.PciInventory);
        Assert.Empty(discovery.Cards);
        Assert.Null(discovery.Devices[0].Connection);
        Assert.Null(discovery.Devices[0].Inventory!.DeviceGroupId);
    }

    [Fact]
    public void PciAndSdkAvailabilityAreIndependentAndOldHelpersRemainCompatible()
    {
        var root = Root(Node()); root["available"] = false; root["error"] = "SDK unavailable";
        var snapshot = Parse(root);
        Assert.False(snapshot.Available); Assert.True(snapshot.PciInventory!.Available);
        var discovery = DeckLinkInventory.Enrich(new(true, true, [new("exact FFmpeg source")]), snapshot);
        Assert.True(discovery.Available); Assert.Equal("exact FFmpeg source", Assert.Single(discovery.Devices).Name);
        Assert.True(discovery.PciInventory!.Available);
        Assert.Equal("SDK unavailable", discovery.InventoryError);
        Assert.Null(DeckLinkInventory.Parse("{\"available\":true,\"devices\":[]}").PciInventory);
        var empty = Parse(Root()); Assert.True(empty.PciInventory!.Available); Assert.Empty(empty.PciInventory.Devices);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"available\":true,\"devices\":{}}")]
    [InlineData("{\"available\":\"yes\",\"devices\":[]}")]
    [InlineData("{\"available\":false,\"devices\":[{}]}")]
    public void MalformedOptionalPciDoesNotDiscardSuccessfulSdkInventory(string json)
    {
        var root = Root(); root["pciInventory"] = JsonNode.Parse(json);
        var snapshot = Parse(root);
        Assert.True(snapshot.Available);
        Assert.False(snapshot.PciInventory!.Available);
        Assert.Empty(snapshot.PciInventory.Devices);
        Assert.NotNull(snapshot.PciInventory.Error);
    }

    [Fact]
    public void RejectsUnboundedNodesParentsAndPropertiesWithoutPublishingPartialDiagnostics()
    {
        var tooMany = Root(Enumerable.Range(0, 65).Select(_ => Node()).ToArray());
        Assert.False(Parse(tooMany).PciInventory!.Available);
        var node = Node(); node["parents"] = new JsonArray(Enumerable.Range(0, 9).Select(_ => (JsonNode)Node()).ToArray());
        Assert.False(Parse(Root(node)).PciInventory!.Available);
        node = Node(); node["hardwareIds"] = new JsonArray(Enumerable.Range(0, 17).Select(_ => (JsonNode)JsonValue.Create(@"PCI\VEN_BDBD&DEV_AAAA")!).ToArray());
        Assert.False(Parse(Root(node)).PciInventory!.Available);
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_AAAA")]
    [InlineData(@"PCI\VEN_BDBD0&DEV_AAAA")]
    [InlineData(@"USB\VEN_BDBD&DEV_AAAA")]
    public void PciRootRecordsRequireExactBlackmagicVendorEvidence(string hardwareId)
    {
        var node = Node(); node["hardwareIds"] = new JsonArray(hardwareId);
        Assert.False(Parse(Root(node)).PciInventory!.Available);
    }

    [Fact]
    public void MissingInvalidAndInconsistentAddressFactsStayUnknown()
    {
        var node = Node(); node["address"] = uint.MaxValue; node["busNumber"] = null;
        node["functionNumber"] = 9; node["uiNumber"] = -1;
        var result = Assert.Single(Parse(Root(node)).PciInventory!.Devices);
        Assert.Null(result.Address); Assert.Null(result.DeviceNumber); Assert.Null(result.FunctionNumber);
        Assert.Null(result.BusNumber); Assert.Null(result.UiNumber);
        node = Node(); node["deviceNumber"] = 11;
        result = Assert.Single(Parse(Root(node)).PciInventory!.Devices);
        Assert.NotNull(result.Address); Assert.Null(result.DeviceNumber); Assert.Null(result.FunctionNumber);
    }

    [Fact]
    public void TruncationAndZeroSdkTopologyRemainExplicitDiagnosticEvidence()
    {
        var node = Node(); node["parentsTruncated"] = true;
        var root = Root(node); root["pciInventory"]!["truncated"] = true;
        root["devices"] = new JsonArray(new JsonObject { ["deviceHandle"] = "exact", ["topologicalId"] = "0x0000000000000000" });
        var snapshot = Parse(root);
        Assert.Equal("0x0000000000000000", Assert.Single(snapshot.Devices).TopologicalId);
        Assert.True(snapshot.PciInventory!.Truncated); Assert.True(Assert.Single(snapshot.PciInventory.Devices).ParentsTruncated);
        Assert.Null(snapshot.Devices[0].DeviceGroupId);
    }
}
