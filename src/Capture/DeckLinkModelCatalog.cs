namespace DeckCapture.Capture;

public sealed record DeckLinkModelDefinition(string Id, string ModelName, DeckLinkConnector[] Connectors,
    string? SourceUrl, string? SourcePage, bool VendorVerified = true);

/// <summary>Documented rear-bracket layouts. New revisions require their own exact model and primary evidence.</summary>
public static class DeckLinkModelCatalog
{
    private const string Profiles = "https://sdk-doc.blackmagicdesign.com/decklink-sdk/HighLevel/profiles.html";
    private const string Legacy = "https://www.artisantg.com/info/Blackmagic_Design_DeckLink_HDPro_PCIe_Manual_2021310104551.pdf";
    // Fresh values prevent callers from mutating a shared layout used by subsequent requests.
    public static DeckLinkModelDefinition[] Models => [
        new("decklink-duo-original", "DeckLink Duo (original)", FixedPairs(2), Legacy, "57"),
        new("decklink-duo-2", "DeckLink Duo 2", Bidirectional(4), Profiles, "SDK 16: 32"),
        new("decklink-quad-original", "DeckLink Quad (original)", FixedPairs(4), Legacy, "59"),
        new("decklink-quad-2", "DeckLink Quad 2", Bidirectional(8), Profiles, "SDK 16: 32"),
        new("decklink-8k-pro", "DeckLink 8K Pro", Bidirectional(4), Profiles, "SDK 16: 31"),
        new("custom", "Other / custom card", [], null, null, false)
    ];
    public static DeckLinkModelDefinition? Find(string? id) => Models.FirstOrDefault(model => model.Id == id);
    internal static DeckLinkModelDefinition? FindSdkModel(string? model) => model switch
    {
        "DeckLink Duo 2" => Find("decklink-duo-2"), "DeckLink Quad 2" => Find("decklink-quad-2"),
        "DeckLink 8K Pro" => Find("decklink-8k-pro"), _ => null
    };
    private static DeckLinkConnector[] Bidirectional(int count) => Enumerable.Range(1, count).Reverse()
        .Select(number => new DeckLinkConnector($"sdi-{number}", $"SDI {number}", "SDI", "Bidirectional"))
        .Append(new("reference", "Reference", "Reference", "Input")).ToArray();
    private static DeckLinkConnector[] FixedPairs(int count) => Enumerable.Range(1, count).Reverse()
        .SelectMany(number => new DeckLinkConnector[] {
            new($"sdi-{number}-out", $"SDI {number} output", "SDI", "Output"),
            new($"sdi-{number}-in", $"SDI {number} input", "SDI", "Input") })
        .Append(new("reference", "Reference", "Reference", "Input")).ToArray();

    // These are profile-assigned capture connector groups, not evidence of signal lock or
    // current single/dual/quad-link mode. The 8K Pro can assign several input BNCs to one endpoint.
    internal static string[] SdkInputConnectors(string? model, string? profile, int? index, int? count)
    {
        if (index is null or < 0 || (count.HasValue && index >= count)) return [];
        var connector = (model, profile, index) switch
        {
            ("DeckLink Duo 2", "2dhd", 0) => 1, ("DeckLink Duo 2", "2dhd", 1) => 3,
            ("DeckLink Duo 2", "2dhd", 2) => 2, ("DeckLink Duo 2", "2dhd", 3) => 4,
            ("DeckLink Duo 2", "1dfd", 0) => 1, ("DeckLink Duo 2", "1dfd", 1) => 3,
            ("DeckLink Quad 2", "2dhd", 0) => 1, ("DeckLink Quad 2", "2dhd", 1) => 3,
            ("DeckLink Quad 2", "2dhd", 2) => 5, ("DeckLink Quad 2", "2dhd", 3) => 7,
            ("DeckLink Quad 2", "2dhd", 4) => 2, ("DeckLink Quad 2", "2dhd", 5) => 4,
            ("DeckLink Quad 2", "2dhd", 6) => 6, ("DeckLink Quad 2", "2dhd", 7) => 8,
            ("DeckLink Quad 2", "1dfd", 0) => 1, ("DeckLink Quad 2", "1dfd", 1) => 3,
            ("DeckLink Quad 2", "1dfd", 2) => 5, ("DeckLink Quad 2", "1dfd", 3) => 7,
            ("DeckLink 8K Pro", "4dhd", 0) => 1, ("DeckLink 8K Pro", "4dhd", 1) => 3,
            ("DeckLink 8K Pro", "4dhd", 2) => 2, ("DeckLink 8K Pro", "4dhd", 3) => 4,
            ("DeckLink 8K Pro", "2dfd", 0) => 1, ("DeckLink 8K Pro", "2dfd", 1) => 3,
            _ => 0
        };
        if (connector != 0) return [$"sdi-{connector}"];
        return (model, profile, index) switch
        {
            ("DeckLink 8K Pro", "1dfd", 0) => ["sdi-1", "sdi-2"],
            ("DeckLink 8K Pro", "1dhd", 0) => ["sdi-1", "sdi-2", "sdi-3", "sdi-4"],
            _ => []
        };
    }
}
