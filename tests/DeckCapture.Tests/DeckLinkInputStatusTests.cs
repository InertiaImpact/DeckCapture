using System.Text.Json;
using DeckCapture.Capture;

namespace DeckCapture.Tests;

public sealed class DeckLinkInputStatusTests
{
    private static DeckLinkInputInventory Parse(object? status, bool includeStatus = true)
    {
        var device = new Dictionary<string, object?> { ["deviceHandle"] = "exact-input", ["modelName"] = "DeckLink Duo 2",
            ["subDeviceIndex"] = 0, ["subDeviceCount"] = 4, ["profileId"] = "1dfd",
            ["videoInputConnections"] = 1, ["selectedVideoInputConnection"] = 1 };
        if (includeStatus) device["status"] = status;
        return Assert.Single(DeckLinkInventory.Parse(JsonSerializer.Serialize(new { available = true, devices = new[] { device } })).Devices);
    }
    private static Dictionary<string, object?> Status(string lockResult = "0x00000000", object? locked = null,
        string detectedResult = "0x00000000", object? detected = null, string currentResult = "0x00000000", object? current = null) => new()
    {
        ["interfaceHResult"] = "0x00000000", ["videoInputSignalLocked"] = locked ?? true, ["videoInputSignalLockedHResult"] = lockResult,
        ["detectedVideoInputMode"] = detected ?? "Hi59", ["detectedVideoInputModeHResult"] = detectedResult,
        ["currentVideoInputMode"] = current ?? "Hi50", ["currentVideoInputModeHResult"] = currentResult
    };

    [Fact]
    public void DetectedSourceModeRemainsSeparateFromConfiguredCaptureMode()
    {
        var inventory = Parse(Status());
        var status = Assert.IsType<DeckLinkInputStatus>(inventory.Status);
        Assert.True(status.VideoInputSignalLocked);
        Assert.Equal("Hi59", status.DetectedVideoInputMode);
        Assert.Equal("Hi50", status.CurrentVideoInputMode);
        Assert.Equal("0x00000000", status.DetectedVideoInputModeHResult);
        Assert.Equal("exact-input", inventory.DeviceHandle);
        Assert.Equal("sdi-1", inventory.PhysicalConnectorId);
    }

    [Fact]
    public void SuccessfulFalseIsAReportedValueRatherThanUnavailableStatus()
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(Status(locked: false)).Status);
        Assert.False(status.VideoInputSignalLocked);
        Assert.Equal("0x00000000", status.VideoInputSignalLockedHResult);
    }

    [Theory]
    [InlineData("0x80004001")]
    [InlineData("0x80004005")]
    [InlineData("0x80070057")]
    [InlineData("0x00000001")]
    public void UnsupportedFailedOrNonSuccessCallsDiscardTheirOutParameters(string result)
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(Status(result, false, result, "Hi59", result, "Hi50")).Status);
        Assert.Null(status.VideoInputSignalLocked);
        Assert.Null(status.DetectedVideoInputMode);
        Assert.Null(status.CurrentVideoInputMode);
        Assert.Equal(result, status.VideoInputSignalLockedHResult);
        Assert.Equal(result, status.DetectedVideoInputModeHResult);
        Assert.Equal(result, status.CurrentVideoInputModeHResult);
    }

    [Fact]
    public void FailedInterfaceCannotBeOverriddenByClaimedGetterSuccess()
    {
        var input = Status(); input["interfaceHResult"] = "0x80004002";
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(input).Status);
        Assert.Equal("0x80004002", status.InterfaceHResult);
        Assert.Null(status.VideoInputSignalLocked);
        Assert.Null(status.DetectedVideoInputMode);
        Assert.Null(status.CurrentVideoInputMode);
    }

    [Fact]
    public void UnattemptedGettersRetainNullResults()
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(new { interfaceHResult = "0x80004002" }).Status);
        Assert.Null(status.VideoInputSignalLockedHResult);
        Assert.Null(status.DetectedVideoInputModeHResult);
        Assert.Null(status.CurrentVideoInputModeHResult);
        Assert.Null(status.VideoInputSignalLocked);
    }

    [Fact]
    public void OlderMissingAndMalformedOptionalStatusPreserveUsableSdkInventory()
    {
        foreach (var input in new object?[] { null, false, "unexpected", new { interfaceHResult = 0 }, new { interfaceHResult = "success" } })
        {
            var inventory = Parse(input);
            Assert.Null(inventory.Status);
            Assert.Equal("exact-input", inventory.DeviceHandle);
            Assert.Equal("sdi-1", inventory.PhysicalConnectorId);
        }
        Assert.Null(Parse(null, includeStatus: false).Status);
    }

    [Fact]
    public void StatusNeverCoercesFlagNumbersOrAcceptsUnboundedModeText()
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(Status(locked: 0, detected: new string('x', 513), current: 123)).Status);
        Assert.Null(status.VideoInputSignalLocked);
        Assert.Null(status.DetectedVideoInputMode);
        Assert.Null(status.CurrentVideoInputMode);
    }

    [Fact]
    public void PaddedFourCcAndUnknownRawValuesArePreservedWithoutInventingAFormat()
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(Status(detected: "pal ", current: "0x0000000000000000")).Status);
        Assert.Equal("pal ", status.DetectedVideoInputMode);
        Assert.Equal("0x0000000000000000", status.CurrentVideoInputMode);
    }

    [Fact]
    public void OneFailedGetterDoesNotHideOtherSuccessfulStatus()
    {
        var status = Assert.IsType<DeckLinkInputStatus>(Parse(Status(detectedResult: "0x80004001")).Status);
        Assert.True(status.VideoInputSignalLocked);
        Assert.Null(status.DetectedVideoInputMode);
        Assert.Equal("0x80004001", status.DetectedVideoInputModeHResult);
        Assert.Equal("Hi50", status.CurrentVideoInputMode);
    }

    [Fact]
    public void ExactHandleEnrichmentCarriesStatusWithoutChangingSdkGroupingOrCaptureKey()
    {
        var inventory = Parse(Status());
        var result = DeckLinkInventory.Enrich(new(true, true, [new("exact-input"), new("different")]), new(true, [inventory]));
        Assert.Equal(inventory.Status, result.Devices[0].Inventory!.Status);
        Assert.Equal("exact-input", result.Devices[0].Name);
        Assert.Null(result.Devices[1].Inventory);
        Assert.Null(inventory.DeviceGroupId);
        Assert.Null(result.Devices[0].Connection);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void DetectionCapabilityPreservesTrueFalseAndUnavailable(bool? supported)
    {
        var snapshot = DeckLinkInventory.Parse(JsonSerializer.Serialize(new { available = true,
            devices = new[] { new { deviceHandle = "exact", supportsInputFormatDetection = supported } } }));
        Assert.Equal(supported, Assert.Single(snapshot.Devices).SupportsInputFormatDetection);
    }

    [Fact]
    public void DetectionCapabilityDoesNotInferSupportFromMissingOrMalformedFlags()
    {
        var old = DeckLinkInventory.Parse("{\"available\":true,\"devices\":[{\"deviceHandle\":\"exact\"}]}");
        var malformed = DeckLinkInventory.Parse("{\"available\":true,\"devices\":[{\"deviceHandle\":\"exact\",\"supportsInputFormatDetection\":1}]}");
        Assert.Null(Assert.Single(old.Devices).SupportsInputFormatDetection);
        Assert.Null(Assert.Single(malformed.Devices).SupportsInputFormatDetection);
    }

}
