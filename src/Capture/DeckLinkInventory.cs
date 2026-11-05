using System.ComponentModel;
using System.Text.Json;

namespace DeckCapture.Capture;

public sealed record DeckLinkInputInventory(string DeviceHandle, string? DeviceGroupId, string? PersistentId,
    string? ModelName, string? DisplayName, int? SubDeviceIndex, int? SubDeviceCount, string? ProfileId,
    string[] SupportedVideoConnections, string? SelectedVideoConnection, string? PhysicalConnectorId)
{
    public string? TopologicalId { get; init; }
    public long? DeviceInterface { get; init; }
    public DeckLinkInputStatus? Status { get; init; }
    public bool? SupportsInputFormatDetection { get; init; }
}
public sealed record DeckLinkInputStatus(string InterfaceHResult, bool? VideoInputSignalLocked, string? VideoInputSignalLockedHResult,
    string? DetectedVideoInputMode, string? DetectedVideoInputModeHResult, string? CurrentVideoInputMode, string? CurrentVideoInputModeHResult);
public sealed record DeckLinkConnector(string Id, string Label, string Kind, string Direction = "Bidirectional");
public sealed record DeckLinkCardInput(string DeviceName, string ConnectorId, bool Present, bool Applied = true);
public sealed record DeckLinkCardInventory(string Id, string? ModelName, DeckLinkConnector[] ConnectorLayout)
{
    public string? ModelId { get; init; }
    public string? Label { get; init; }
    public string MappingSource { get; init; } = "Sdk";
    public DeckLinkCardInput[] Inputs { get; init; } = [];
}
internal sealed record DeckLinkInventorySnapshot(bool Available, DeckLinkInputInventory[] Devices, string? Error = null)
{
    public WindowsPciInventory? PciInventory { get; init; }
}

/// <summary>Optional isolated SDK metadata. Capture source names always come from FFmpeg, never this helper.</summary>
internal static class DeckLinkInventory
{
    private static readonly SemaphoreSlim InventoryGate = new(1, 1);
    internal static async Task<DeckLinkInventorySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(false, [], "SDK inventory is available on Windows only.");
        var helper = Path.Combine(AppContext.BaseDirectory, "media-tools", "DeckCapture.DeviceInventory.exe");
        if (!File.Exists(helper)) return new(false, [], "Optional SDK inventory helper is not bundled; physical card and connector mapping is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var acquired = false;
        try
        {
            await InventoryGate.WaitAsync(timeout.Token);
            acquired = true;
            // Reuse the bounded, ArgumentList-only, job-contained process runner. The native helper
            // has no arguments, configuration writes, input-enable calls or profile changes.
            var result = await InputDiscovery.RunProcessAsync(helper, [], timeout.Token);
            if (result.ExitCode != 0) return new(false, [], "SDK inventory helper failed (exit " + result.ExitCode + ").");
            return Parse(result.StandardOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, [], "SDK inventory exceeded its 5-second deadline."); }
        catch (Exception exception) when (IsOptionalFailure(exception))
        { return new(false, [], "SDK inventory could not be read; physical card and connector mapping is unavailable."); }
        finally { if (acquired) InventoryGate.Release(); }
    }

    internal static bool IsOptionalFailure(Exception exception) => exception is IOException or InvalidDataException or Win32Exception or InvalidOperationException
        or ArgumentException or JsonException or KeyNotFoundException or FormatException or OverflowException;

    internal static DeckLinkInventorySnapshot Parse(string json)
    {
        if (json.Length > 131072) throw new InvalidDataException("SDK inventory output exceeds its limit.");
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        var root = document.RootElement;
        var pci = WindowsPciInventoryParser.ParseOptional(root);
        if (!root.GetProperty("available").GetBoolean())
            return new(false, [], Text(root, "error") ?? "The installed DeckLink driver did not provide SDK inventory.") { PciInventory = pci };
        var array = root.GetProperty("devices");
        if (array.GetArrayLength() > 256) throw new InvalidDataException("SDK inventory exceeds 256 devices.");
        var devices = new List<DeckLinkInputInventory>();
        foreach (var item in array.EnumerateArray())
        {
            var handle = Text(item, "deviceHandle");
            if (handle is null) continue; // No exact FFmpeg join key: do not infer from model/order.
            var model = Text(item, "modelName");
            var index = Number(item, "subDeviceIndex");
            var count = Number(item, "subDeviceCount");
            var profile = Text(item, "profileId");
            var mask = Long(item, "videoInputConnections");
            var selected = Long(item, "selectedVideoInputConnection");
            var connections = ConnectionNames(mask ?? 0);
            var selectedNames = ConnectionNames(selected ?? 0);
            var selectedConnection = selectedNames.Length == 1 ? selectedNames[0] : null;
            devices.Add(new(handle, Identity(item, "deviceGroupId"), Identity(item, "persistentId"), model,
                Text(item, "displayName"), index, count, profile, connections, selectedConnection,
                // Connector maps describe SDI inputs. Other selections cannot inherit the map.
                selected == 1 && (mask.GetValueOrDefault() & 1) != 0 ? Duo2Connector(model, profile, index, count) : null)
            { TopologicalId = Identity(item, "topologicalId", allowZero: true), DeviceInterface = Long(item, "deviceInterface"), Status = ReadStatus(item),
                SupportsInputFormatDetection = Boolean(item, "supportsInputFormatDetection") });
        }
        // Duplicate join keys are ambiguous, even if their descriptions happen to match.
        var unique = devices.GroupBy(device => device.DeviceHandle, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        return new(true, unique) { PciInventory = pci };
    }

    internal static InputDiscoveryResult Enrich(InputDiscoveryResult discovery, DeckLinkInventorySnapshot snapshot)
    {
        if (!snapshot.Available) return discovery with { InventoryError = snapshot.Error, PciInventory = snapshot.PciInventory };
        var byHandle = snapshot.Devices.ToDictionary(device => device.DeviceHandle, StringComparer.Ordinal);
        var devices = discovery.Devices.Select(device =>
        {
            if (!byHandle.TryGetValue(device.Name, out var metadata)) return device;
            var connectors = metadata.SelectedVideoConnection == "SDI" && metadata.SupportedVideoConnections.Contains("SDI", StringComparer.Ordinal)
                ? DeckLinkModelCatalog.SdkInputConnectors(metadata.ModelName, metadata.ProfileId, metadata.SubDeviceIndex, metadata.SubDeviceCount) : [];
            return device with { Inventory = metadata, Connection = metadata.DeviceGroupId is { } group && connectors.Length > 0
                ? new(group, connectors, "Sdk") : null };
        }).ToArray();
        var cards = devices.Where(device => device.Inventory?.DeviceGroupId is not null)
            .GroupBy(device => device.Inventory!.DeviceGroupId!, StringComparer.Ordinal)
            .Select(group =>
            {
                var models = group.Select(device => device.Inventory!.ModelName).Distinct(StringComparer.Ordinal).ToArray();
                var model = models.Length == 1 ? models[0] : null;
                var definition = DeckLinkModelCatalog.FindSdkModel(model);
                return new DeckLinkCardInventory(group.Key, model, definition?.Connectors ?? [])
                { ModelId = definition?.Id, Label = model, Inputs = group.Where(device => device.Connection is not null)
                    .SelectMany(device => device.Connection!.ConnectorIds.Select(connector => new DeckLinkCardInput(device.Name, connector, true))).ToArray() };
            }).ToArray();
        return discovery with { Devices = devices, Cards = cards, PciInventory = snapshot.PciInventory, InventoryError = devices.Any(device => device.Inventory is null)
            ? "Some FFmpeg inputs could not be matched to an exact SDK device handle; their physical mapping is unknown." : null };
    }

    // Keep the legacy single-connector field compatible while sharing the catalog's
    // authoritative mapping table with the new multi-connector connection projection.
    internal static string? Duo2Connector(string? model, string? profile, int? index, int? count)
    {
        if (model != "DeckLink Duo 2") return null;
        var connectors = DeckLinkModelCatalog.SdkInputConnectors(model, profile, index, count);
        return connectors.Length == 1 ? connectors[0] : null;
    }
    private static bool? Boolean(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static DeckLinkInputStatus? ReadStatus(JsonElement item)
    {
        if (!item.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object) return null;
        var queried = StatusHResult(status, "interfaceHResult");
        if (queried is null) return null; // Malformed optional diagnostics cannot invalidate a usable source.
        var lockResult = StatusHResult(status, "videoInputSignalLockedHResult");
        var detectedResult = StatusHResult(status, "detectedVideoInputModeHResult");
        var currentResult = StatusHResult(status, "currentVideoInputModeHResult");
        // Both QueryInterface and the individual getter must succeed. Stale out-parameters
        // on E_NOTIMPL/E_FAIL/S_FALSE must never become a measured false or a detected mode.
        var available = queried == "0x00000000";
        bool? locked = available && lockResult == "0x00000000" && status.TryGetProperty("videoInputSignalLocked", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
        return new(queried, locked, lockResult, available && detectedResult == "0x00000000" ? StatusMode(status, "detectedVideoInputMode") : null,
            detectedResult, available && currentResult == "0x00000000" ? StatusMode(status, "currentVideoInputMode") : null, currentResult);
    }
    private static string? StatusHResult(JsonElement status, string name)
    {
        if (!status.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: 10 } && text.StartsWith("0x", StringComparison.Ordinal) &&
            uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var result) ? "0x" + result.ToString("x8") : null;
    }
    private static string? StatusMode(JsonElement status, string name)
    {
        if (!status.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (text is { Length: 4 } && text.All(character => character is >= ' ' and <= '~')) return text;
        return text is { Length: 18 } && text.StartsWith("0x", StringComparison.Ordinal) &&
            ulong.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var raw) ? "0x" + raw.ToString("x16") : null;
    }
    private static string[] ConnectionNames(long mask)
    {
        string[] names = ["SDI", "HDMI", "Optical SDI", "Component", "Composite", "S-Video", "Ethernet", "Optical Ethernet", "Internal"];
        return names.Where((_, index) => (mask & (1L << index)) != 0).ToArray();
    }
    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        if (text is null || text.Length == 0 || text.Length > 512 || text.Any(char.IsControl)) return null;
        return text;
    }
    private static string? Identity(JsonElement item, string name, bool allowZero = false)
    {
        var value = Text(item, name);
        return value is { Length: 18 } && value.StartsWith("0x", StringComparison.Ordinal) &&
            ulong.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var id) && (allowZero || id != 0)
            ? "0x" + id.ToString("x16") : null;
    }
    private static long? Long(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : null;
    private static int? Number(JsonElement item, string name)
    {
        var value = Long(item, name);
        return value is >= 0 and <= 256 ? (int)value.Value : null;
    }
}
