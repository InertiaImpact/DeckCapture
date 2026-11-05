using System.Text.Json;

namespace DeckCapture.Capture;

public sealed record DeckLinkFilterIdentity(string? FriendlyName, string? DevicePath, string? PnpInstanceId,
    string? TopologicalId, string? PersistentId, string? Error)
{
    public string? InterfaceInstanceId { get; init; }
    public string[] InterfaceAncestorInstanceIds { get; init; } = [];
    public bool InterfaceAncestryTruncated { get; init; }
}
public sealed record DeckLinkEndpointCorrelation(string DeviceHandle, string? TopologicalId, string? PersistentId,
    string Status, string? MatchMethod, string? PnpInstanceId, DeckLinkFilterIdentity[] Matches);
public sealed record DeckLinkPciCorrelationResult(bool Available, string? Error = null)
{
    public bool Truncated { get; init; }
    public DeckLinkInputInventory[] SdkDevices { get; init; } = [];
    public WindowsPciInventory? PciInventory { get; init; }
    public DeckLinkFilterIdentity[] Filters { get; init; } = [];
    public DeckLinkEndpointCorrelation[] Endpoints { get; init; } = [];
}

/// <summary>Explicit idle-only identity probe. The caller must reserve all input hardware before calling it.</summary>
public static class DeckLinkPciCorrelation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static Task<DeckLinkPciCorrelationResult> ProbeIdleAsync(CancellationToken cancellationToken) =>
        ProbeAsync(InputDiscovery.RunProcessAsync, OperatingSystem.IsWindows()
            ? Path.Combine(AppContext.BaseDirectory, "media-tools", "DeckCapture.DeviceInventory.exe") : null, cancellationToken);

    internal static async Task<DeckLinkPciCorrelationResult> ProbeAsync(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<DiscoveryProcessResult>> runner,
        string? helper, CancellationToken cancellationToken, TimeSpan? timeoutForTest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (helper is null || !File.Exists(helper)) return new(false, "The optional Windows identity helper is not bundled.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutForTest ?? TimeSpan.FromSeconds(15));
        var acquired = false;
        try
        {
            await Gate.WaitAsync(timeout.Token); acquired = true;
            // Fixed argv only: no endpoint names, executable choices or arbitrary helper flags in the API.
            var result = await runner(helper, ["--pcie-correlate-idle"], timeout.Token);
            if (result.ExitCode != 0) return new(false, "Idle identity helper failed (exit " + result.ExitCode + ").");
            return Parse(result.StandardOutput);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "Idle PCI identity probe exceeded its 15-second deadline."); }
        catch (Exception exception) when (DeckLinkInventory.IsOptionalFailure(exception))
        { return new(false, "Idle PCI identity probe could not be read. No association was accepted."); }
        finally { if (acquired) Gate.Release(); }
    }

    internal static DeckLinkPciCorrelationResult Parse(string json)
    {
        var sdk = DeckLinkInventory.Parse(json);
        using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        var root = document.RootElement;
        if (!root.TryGetProperty("directShowCorrelation", out var correlation))
            return new(false, "The identity helper did not return idle DirectShow correlation data.") { SdkDevices = sdk.Devices, PciInventory = sdk.PciInventory };
        var available = correlation.GetProperty("available").GetBoolean();
        var array = correlation.GetProperty("filters");
        if (array.GetArrayLength() > 256) throw new InvalidDataException("Too many DirectShow identity records.");
        var filters = array.EnumerateArray().Select(item => new DeckLinkFilterIdentity(Text(item, "friendlyName"), Text(item, "devicePath"),
            Pnp(item), Identity(item, "topologicalId"), Identity(item, "persistentId"), Text(item, "error"))
        { InterfaceInstanceId = Text(item, "interfaceInstanceId"), InterfaceAncestorInstanceIds = Ancestors(item),
            InterfaceAncestryTruncated = item.TryGetProperty("interfaceAncestryTruncated", out var ancestryCut) && ancestryCut.GetBoolean() }).ToArray();
        var truncated = (root.TryGetProperty("sdkTruncated", out var sdkCut) && sdkCut.GetBoolean()) ||
            (correlation.TryGetProperty("truncated", out var filterCut) && filterCut.GetBoolean()) || sdk.PciInventory?.Truncated == true;
        var sdkIdentitiesComplete = root.GetProperty("devices").GetArrayLength() == sdk.Devices.Length;
        var complete = sdk.Available && available && !truncated && sdkIdentitiesComplete;
        var endpoints = sdk.Devices.Select(device => Associate(device, sdk.Devices, filters, complete)).ToArray();
        var error = !sdk.Available ? sdk.Error : !available ? Text(correlation, "error") ?? "DirectShow identity enumeration was unavailable."
            : truncated ? "Identity diagnostics were truncated; no exact association was accepted."
            : !sdkIdentitiesComplete ? "SDK endpoint handles were missing or ambiguous; no exact association was accepted." : null;
        return new(sdk.Available && available, error) { SdkDevices = sdk.Devices, PciInventory = sdk.PciInventory,
            Filters = filters, Endpoints = endpoints, Truncated = truncated };
    }

    private static DeckLinkEndpointCorrelation Associate(DeckLinkInputInventory device, DeckLinkInputInventory[] sdk,
        DeckLinkFilterIdentity[] filters, bool complete)
    {
        var persistent = Nonzero(device.PersistentId);
        var topology = Nonzero(device.TopologicalId);
        var candidates = filters.Where(filter => Resolved(filter) &&
            ((persistent is not null && persistent == Nonzero(filter.PersistentId)) || (topology is not null && topology == Nonzero(filter.TopologicalId)))).ToArray();
        string? method = null, instance = null;
        var status = candidates.Length == 0 ? "Unmatched" : "Ambiguous";
        if (complete)
        {
            var byPersistent = persistent is null ? [] : filters.Where(filter => Resolved(filter) && persistent == Nonzero(filter.PersistentId)).ToArray();
            var byTopology = topology is null ? [] : filters.Where(filter => Resolved(filter) && topology == Nonzero(filter.TopologicalId)).ToArray();
            var uniquePersistent = byPersistent.Length == 1 && sdk.Count(item => Nonzero(item.PersistentId) == persistent) == 1;
            var uniqueTopology = byTopology.Length == 1 && sdk.Count(item => Nonzero(item.TopologicalId) == topology) == 1;
            var conflict = candidates.Any(filter => (persistent is not null && Nonzero(filter.PersistentId) is { } otherPersistent && persistent != otherPersistent) ||
                (topology is not null && Nonzero(filter.TopologicalId) is { } otherTopology && topology != otherTopology));
            if (conflict || (uniquePersistent && uniqueTopology && byPersistent[0] != byTopology[0])) status = "Conflict";
            else if (uniquePersistent || uniqueTopology)
            {
                var filter = uniquePersistent ? byPersistent[0] : byTopology[0];
                method = uniquePersistent ? "PersistentId" : "TopologicalId"; instance = filter.PnpInstanceId; status = "ExactIdMatch";
            }
        }
        return new(device.DeviceHandle, device.TopologicalId, device.PersistentId, status, method, instance, candidates);
    }

    private static bool Resolved(DeckLinkFilterIdentity filter)
    {
        if (filter.PnpInstanceId is null || filter.DevicePath is null || filter.InterfaceAncestryTruncated) return false;
        if (filter.InterfaceInstanceId is null) return true; // Older immediate-PCI helper reports remain readable.
        var firstPci = new[] { filter.InterfaceInstanceId }.Concat(filter.InterfaceAncestorInstanceIds)
            .FirstOrDefault(id => id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase));
        return firstPci?.Equals(filter.PnpInstanceId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string[] Ancestors(JsonElement item)
    {
        if (!item.TryGetProperty("interfaceAncestorInstanceIds", out var array)) return [];
        if (array.GetArrayLength() > 8) throw new InvalidDataException("Interface ancestry exceeds eight nodes.");
        var result = new List<string>();
        foreach (var value in array.EnumerateArray())
        {
            var id = value.GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 512 || id.Any(char.IsControl)) throw new InvalidDataException("Invalid interface ancestor identity.");
            result.Add(id);
        }
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count ||
            (Text(item, "interfaceInstanceId") is { } own && result.Contains(own, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("Interface ancestry contains a cycle.");
        return result.ToArray();
    }

    private static string? Nonzero(string? value) => value is not null && value != "0x0000000000000000" ? value : null;
    private static string? Pnp(JsonElement item)
    {
        var value = Text(item, "pnpInstanceId");
        // Native resolution validates the registered interface's exact hardware IDs before binding.
        // Retain that conservative vendor constraint when parsing helper output too.
        const string prefix = "PCI\\VEN_BDBD";
        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true && value.Length > prefix.Length && value[prefix.Length] == '&' ? value : null;
    }
    private static string? Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        return !string.IsNullOrEmpty(text) && text.Length <= 512 && !text.Any(char.IsControl) ? text : null;
    }
    private static string? Identity(JsonElement item, string name)
    {
        var value = Text(item, name);
        return value is { Length: 18 } && value.StartsWith("0x", StringComparison.Ordinal) &&
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var number) ? "0x" + number.ToString("x16") : null;
    }
}
