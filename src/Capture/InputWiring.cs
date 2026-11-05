using DeckCapture.Domain;

namespace DeckCapture.Capture;

/// <summary>Applies explicitly saved physical annotations without changing any capture source key or SDK evidence.</summary>
public static class InputWiring
{
    public static InputDiscoveryResult Apply(InputDiscoveryResult discovery, IReadOnlyList<PhysicalInputWiring> wiring)
    {
        var devices = discovery.Devices.ToDictionary(device => device.Name, StringComparer.Ordinal);
        var cards = discovery.Cards.ToList();
        var warnings = new List<string>();
        var groupClaims = wiring.Select(card => PresentGroup(card, devices)).Where(group => group is not null)
            .GroupBy(group => group!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var card in wiring)
        {
            var definition = DeckLinkModelCatalog.Find(card.ModelId);
            if (definition is null) continue; // Saved configuration is validated before it reaches discovery.
            var connectors = card.ModelId == "custom" ? card.CustomConnectors.Select(port =>
                new DeckLinkConnector(port.Id, port.Label, port.Kind, port.Direction)).ToArray() : definition.Connectors;
            var group = PresentGroup(card, devices);
            var touchesSdkGroup = card.Inputs.Any(binding => devices.TryGetValue(binding.DeviceName, out var device) && device.Inventory?.DeviceGroupId is not null);
            var sdkCard = group is null ? null : discovery.Cards.FirstOrDefault(candidate => candidate.Id == group);
            // An unknown SDK model may receive an operator layout while preserving the SDK's card ID.
            // Never replace a vendor layout, known connector map, or combine physical SDK groups.
            var overlay = group is not null && groupClaims[group] == 1 && (sdkCard is null || sdkCard.ConnectorLayout.Length == 0) &&
                !discovery.Devices.Any(device => device.Inventory?.DeviceGroupId == group && HasSdkConnector(device));
            var blocked = touchesSdkGroup && !overlay;
            var id = overlay ? group! : "operator:" + card.Id;
            var bindings = new List<DeckLinkCardInput>();
            foreach (var binding in card.Inputs)
            {
                var present = devices.TryGetValue(binding.DeviceName, out var device);
                var applied = present && !blocked && !HasSdkConnector(device!);
                bindings.Add(new(binding.DeviceName, binding.ConnectorId, present, applied));
                if (applied) devices[binding.DeviceName] = device! with { Connection = new(id, [binding.ConnectorId], "Operator") };
                else if (present) warnings.Add("An operator wiring entry conflicts with SDK card metadata and was not applied.");
                else warnings.Add("An operator-mapped endpoint is absent; its saved wiring was retained without reassignment.");
            }
            var annotated = new DeckLinkCardInventory(id, definition.ModelName, connectors)
            { ModelId = card.ModelId, Label = card.Label, MappingSource = "Operator", Inputs = bindings.ToArray() };
            if (overlay) cards.RemoveAll(candidate => candidate.Id == id);
            cards.Add(annotated);
        }
        var warning = string.Join(" ", new[] { discovery.InventoryError }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Concat(warnings.Distinct(StringComparer.Ordinal)));
        return discovery with { Devices = discovery.Devices.Select(device => devices[device.Name]).ToArray(), Cards = cards.ToArray(),
            InventoryError = warning.Length == 0 ? null : warning };
    }

    private static bool HasSdkConnector(DiscoveredInput device) => device.Connection?.MappingSource == "Sdk" || device.Inventory?.PhysicalConnectorId is not null;

    private static string? PresentGroup(PhysicalInputWiring card, IReadOnlyDictionary<string, DiscoveredInput> devices)
    {
        var present = card.Inputs.Where(binding => devices.ContainsKey(binding.DeviceName)).Select(binding => devices[binding.DeviceName]).ToArray();
        if (present.Length == 0) return null;
        var group = present[0].Inventory?.DeviceGroupId;
        return group is not null && present.All(device => device.Inventory?.DeviceGroupId == group) ? group : null;
    }
}
