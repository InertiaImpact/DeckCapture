using System.Text.Json;
using DeckCapture.Capture;
using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

internal static class InputWiringRules
{
    public static void Validate(IReadOnlyList<PhysicalInputWiring>? cards, List<string> errors)
    {
        if (cards is null || cards.Count > 32 || cards.Any(card => card is null))
        { errors.Add("Input wiring needs at most 32 non-null physical cards."); return; }
        var cardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!Identifier(card.Id) || !cardIds.Add(card.Id)) errors.Add("Physical card IDs must be unique letters, digits, hyphens or underscores, up to 40 characters.");
            if (!Text(card.Label, 100)) errors.Add("Physical cards need a label of 1–100 characters without controls.");
            var model = DeckLinkModelCatalog.Find(card.ModelId);
            if (model is null) errors.Add("Choose a supported physical card model or custom.");
            if (card.CustomConnectors is null || card.CustomConnectors.Count > 32 || card.CustomConnectors.Any(port => port is null))
            { errors.Add("Custom layouts need at most 32 non-null connectors."); continue; }
            if (card.ModelId != "custom" && card.CustomConnectors.Count != 0) errors.Add("Custom connectors are allowed only for the custom card model.");
            if (card.ModelId == "custom" && card.CustomConnectors.Count == 0) errors.Add("A custom card needs at least one connector.");
            var portIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in card.CustomConnectors)
            {
                if (!Identifier(port.Id) || !portIds.Add(port.Id) || !Text(port.Label, 80) ||
                    port.Kind is not ("SDI" or "HDMI" or "Optical SDI" or "Component" or "Composite" or "S-Video" or "Reference" or "Other") ||
                    port.Direction is not ("Input" or "Output" or "Bidirectional") || (port.Kind == "Reference" && port.Direction != "Input"))
                    errors.Add("Custom connectors need unique IDs, readable labels, a supported kind and Input, Output or Bidirectional direction.");
            }
            if (card.Inputs is null || card.Inputs.Count > 32 || card.Inputs.Any(binding => binding is null))
            { errors.Add("A physical card needs at most 32 non-null endpoint mappings."); continue; }
            var connectors = card.ModelId == "custom" ? card.CustomConnectors.Select(port => new DeckLinkConnector(port.Id, port.Label, port.Kind, port.Direction)).ToArray()
                : model?.Connectors ?? [];
            var mappedPorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in card.Inputs)
            {
                if (!Text(binding.DeviceName, 512) || !endpoints.Add(binding.DeviceName)) errors.Add("Each exact endpoint name may be mapped once across all physical cards.");
                if (!Text(binding.ConnectorId, 40) || !mappedPorts.Add(binding.ConnectorId)) errors.Add("Each physical input connector may be mapped once.");
                var connector = connectors.FirstOrDefault(port => port.Id == binding.ConnectorId);
                if (connector is null || connector.Kind == "Reference" || connector.Direction is not ("Input" or "Bidirectional"))
                    errors.Add("Map endpoints only to an input or bidirectional connector in the selected card layout; outputs and reference cannot capture.");
            }
        }
    }

    // Job snapshots retain wiring with all other configuration. Do not relabel an active input
    // underneath its operator, but unrelated card/input annotations remain editable.
    public static bool AffectsDevice(AppConfiguration before, AppConfiguration after, string deviceName) =>
        Signature(before, deviceName) != Signature(after, deviceName);
    private static string Signature(AppConfiguration configuration, string deviceName) => JsonSerializer.Serialize(configuration.InputWiring
        .Where(card => card.Inputs.Any(binding => binding.DeviceName == deviceName)).OrderBy(card => card.Id, StringComparer.Ordinal)
        .Select(card => card with { Inputs = card.Inputs.Where(binding => binding.DeviceName == deviceName).ToList() }), StateStore.Json);
    private static bool Identifier(string? value) => Text(value, 40) && value!.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
