using System.Text.Json;

namespace DeckCapture.Capture;

public record WindowsPciNode
{
    public string? InstanceId { get; init; }
    public string? Description { get; init; }
    public string? FriendlyName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Service { get; init; }
    public string[] HardwareIds { get; init; } = [];
    public long? BusNumber { get; init; }
    public long? Address { get; init; }
    public int? DeviceNumber { get; init; }
    public int? FunctionNumber { get; init; }
    public string? LocationInfo { get; init; }
    public string[] LocationPaths { get; init; } = [];
    public long? UiNumber { get; init; }
    public string? ContainerId { get; init; }
    public string? PhysicalDeviceLocation { get; init; }
}
public sealed record WindowsPciDevice : WindowsPciNode
{
    public WindowsPciNode[] Parents { get; init; } = [];
    public bool ParentsTruncated { get; init; }
}
public sealed record WindowsPciInventory(bool Available, WindowsPciDevice[] Devices, bool Truncated = false, string? Error = null);

// Diagnostic evidence only. No method here decodes SDK TopologicalID, matches it to
// Windows addresses, assigns channels, or infers a physical board from parent equality.
internal static class WindowsPciInventoryParser
{
    internal static WindowsPciInventory? ParseOptional(JsonElement root)
    {
        if (!root.TryGetProperty("pciInventory", out var inventory)) return null; // Older helper remains compatible.
        try
        {
            var available = inventory.GetProperty("available").GetBoolean();
            var array = inventory.GetProperty("devices");
            if (array.GetArrayLength() > 64) throw new InvalidDataException("PCI diagnostics exceed 64 devices.");
            var devices = new List<WindowsPciDevice>();
            foreach (var item in array.EnumerateArray())
            {
                var node = Node(item);
                if (node.InstanceId is null || !node.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
                    !node.HardwareIds.Any(IsBlackmagicHardwareId)) throw new InvalidDataException("Unexpected PCI diagnostic node.");
                var parents = item.GetProperty("parents");
                if (parents.GetArrayLength() > 8) throw new InvalidDataException("PCI parent chain exceeds eight nodes.");
                devices.Add(new()
                {
                    InstanceId = node.InstanceId, Description = node.Description, FriendlyName = node.FriendlyName, Manufacturer = node.Manufacturer,
                    Service = node.Service, HardwareIds = node.HardwareIds, BusNumber = node.BusNumber, Address = node.Address,
                    DeviceNumber = node.DeviceNumber, FunctionNumber = node.FunctionNumber, LocationInfo = node.LocationInfo,
                    LocationPaths = node.LocationPaths, UiNumber = node.UiNumber, ContainerId = node.ContainerId,
                    PhysicalDeviceLocation = node.PhysicalDeviceLocation, Parents = parents.EnumerateArray().Select(Node).ToArray(),
                    ParentsTruncated = item.TryGetProperty("parentsTruncated", out var cut) && cut.GetBoolean()
                });
            }
            var truncated = inventory.TryGetProperty("truncated", out var value) && value.GetBoolean();
            if (!available && devices.Count > 0) throw new InvalidDataException("Unavailable PCI diagnostics contain partial devices.");
            return new(available, devices.ToArray(), truncated, Text(inventory, "error"));
        }
        catch (Exception exception) when (DeckLinkInventory.IsOptionalFailure(exception))
        { return new(false, [], Error: "Optional Windows PCI diagnostics were malformed or exceeded their limits."); }
    }

    private static bool IsBlackmagicHardwareId(string value)
    {
        const string prefix = "PCI\\VEN_BDBD";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (value.Length == prefix.Length || value[prefix.Length] == '&');
    }
    private static WindowsPciNode Node(JsonElement item)
    {
        var address = Number(item, "address", uint.MaxValue - 1L);
        var device = Number(item, "deviceNumber", 31);
        var function = Number(item, "functionNumber", 7);
        var id = Text(item, "instanceId");
        // Accept Windows' documented PCI address representation only when all raw fields agree.
        // This is deliberately unrelated to the opaque SDK topology value.
        var addressValid = id?.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) == true && address.HasValue &&
            device == (address >> 16) && function == (address & 0xffff);
        return new()
        {
            InstanceId = id, Description = Text(item, "description"), FriendlyName = Text(item, "friendlyName"),
            Manufacturer = Text(item, "manufacturer"), Service = Text(item, "service"), HardwareIds = Strings(item, "hardwareIds"),
            BusNumber = Number(item, "busNumber", uint.MaxValue - 1L), Address = address,
            DeviceNumber = addressValid ? (int?)device : null, FunctionNumber = addressValid ? (int?)function : null,
            LocationInfo = Text(item, "locationInfo"), LocationPaths = Strings(item, "locationPaths"), UiNumber = Number(item, "uiNumber", uint.MaxValue - 1L),
            ContainerId = Text(item, "containerId"), PhysicalDeviceLocation = Text(item, "physicalDeviceLocation")
        };
    }
    private static string[] Strings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var array)) return [];
        if (array.GetArrayLength() > 16) throw new InvalidDataException("PCI property list exceeds 16 values.");
        return array.EnumerateArray().Select(value => CheckedText(value.GetString())).Where(value => value is not null).Select(value => value!).ToArray();
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? CheckedText(value.GetString()) : null;
    private static string? CheckedText(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 512 && !value.Any(char.IsControl) ? value : null;
    private static long? Number(JsonElement item, string name, long maximum) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 && number <= maximum ? number : null;
}
