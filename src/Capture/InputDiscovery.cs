using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

public sealed record DiscoveredInput(string Name, string? Description = null, DeckLinkInputInventory? Inventory = null,
    DiscoveredInputConnection? Connection = null);
public sealed record DiscoveredInputConnection(string CardId, string[] ConnectorIds, string MappingSource);
public sealed record InputDiscoveryResult(bool Available, bool DeckLinkSupported, DiscoveredInput[] Devices, string? Error = null)
{
    public DeckLinkCardInventory[] Cards { get; init; } = [];
    public string? InventoryError { get; init; }
    public WindowsPciInventory? PciInventory { get; init; }
}
public sealed record DiscoveredInputMode(string Code, string Label);
public sealed record InputFormatsResult(bool Available, bool DeckLinkSupported, string DeviceName, DiscoveredInputMode[] Modes, string? Error = null);
internal sealed record DiscoveryProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Lists real FFmpeg DeckLink sources; it never manufactures inputs from configured channel names.</summary>
public sealed class InputDiscovery
{
    private const int MaximumOutputCharacters = 131072;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<DiscoveryProcessResult>> run;
    private readonly Func<CancellationToken, Task<DeckLinkInventorySnapshot>>? inventory;
    public InputDiscovery() : this(RunProcessAsync, DeckLinkInventory.ReadAsync) { }
    internal InputDiscovery(Func<string, IReadOnlyList<string>, CancellationToken, Task<DiscoveryProcessResult>> runner) => run = runner;
    internal InputDiscovery(Func<string, IReadOnlyList<string>, CancellationToken, Task<DiscoveryProcessResult>> runner,
        Func<CancellationToken, Task<DeckLinkInventorySnapshot>> inventoryReader) { run = runner; inventory = inventoryReader; }

    public async Task<InputDiscoveryResult> DiscoverAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var supported = false;
        try
        {
            var result = await DiscoverCoreAsync(profile, value => supported = value, timeout.Token);
            // Inventory is optional and independent of FFmpeg capability/source success. It never opens an input.
            if (result.Available && result.Devices.Length > 0 && inventory is not null)
            {
                try { return DeckLinkInventory.Enrich(result, await inventory(cancellationToken)); }
                catch (Exception exception) when (DeckLinkInventory.IsOptionalFailure(exception))
                { return result with { InventoryError = "SDK inventory could not be read; physical card and connector mapping is unavailable." }; }
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, supported, [], "DeckLink discovery exceeded its 20-second deadline."); }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or ArgumentException)
        { return new(false, supported, [], "DeckLink discovery failed: " + Compact(exception.Message)); }
    }

    public async Task<InputFormatsResult> DiscoverFormatsAsync(CaptureProfile profile, string deviceName, CancellationToken cancellationToken)
    {
        ValidateDeviceName(deviceName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var supported = false;
        try
        {
            var inputs = await DiscoverCoreAsync(profile, value => supported = value, timeout.Token);
            if (!inputs.Available) return new(false, supported, deviceName, [], inputs.Error);
            if (!inputs.Devices.Any(device => device.Name.Equals(deviceName, StringComparison.Ordinal)))
                return new(false, supported, deviceName, [], "The selected device was not returned by current DeckLink source discovery. Refresh inputs before listing its formats.");
            var result = await run(profile.FfmpegPath, ["-hide_banner", "-nostdin", "-loglevel", "info", "-f", "decklink", "-list_formats", "1", "-i", deviceName], timeout.Token);
            var text = Combine(result);
            var modes = ParseFormats(text);
            // list_formats intentionally exits the input-open path, often with AVERROR_EXIT/nonzero.
            // A recognizable complete table is evidence; an exit code alone is not success or failure.
            if (modes.Length > 0 && text.Contains("Supported formats for", StringComparison.OrdinalIgnoreCase) && !HasHardwareFailure(text))
                return new(true, true, deviceName, modes);
            return new(false, true, deviceName, [], "No usable DeckLink format table was returned. " + FailureDetail(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, supported, deviceName, [], "DeckLink format discovery exceeded its 20-second deadline."); }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or ArgumentException)
        { return new(false, supported, deviceName, [], "DeckLink format discovery failed: " + Compact(exception.Message)); }
    }

    private async Task<InputDiscoveryResult> DiscoverCoreAsync(CaptureProfile profile, Action<bool> setSupported, CancellationToken token)
    {
        var devices = await run(profile.FfmpegPath, ["-hide_banner", "-nostdin", "-devices"], token);
        if (devices.ExitCode != 0) return new(false, false, [], "FFmpeg device capabilities could not be read. " + FailureDetail(devices));
        var supported = Regex.IsMatch(Combine(devices), @"(?m)^\s*D[E. ]*\s+decklink\s", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        setSupported(supported);
        // -sources decklink can silently exit zero on a build without that device. Check the table first.
        if (!supported) return new(false, false, [], "This FFmpeg build does not include DeckLink input support. No hardware inputs were discovered; synthetic channels are separate development sources.");
        var listing = await run(profile.FfmpegPath, ["-hide_banner", "-nostdin", "-sources", "decklink"], token);
        var text = Combine(listing);
        if (listing.ExitCode != 0 || HasHardwareFailure(text))
            return new(false, true, [], "DeckLink source enumeration failed. Check the installed driver, card and service account. " + FailureDetail(listing));
        if (!text.Contains("Auto-detected sources for decklink:", StringComparison.OrdinalIgnoreCase))
            return new(false, true, [], "FFmpeg did not return a recognizable DeckLink source listing; hardware availability is unknown.");
        var inputs = ParseSources(text);
        return new(true, true, inputs, inputs.Length == 0 ? "FFmpeg reported no DeckLink input devices. This does not establish card/driver readiness." : null);
    }

    internal static DiscoveredInput[] ParseSources(string text)
    {
        var names = new Dictionary<string, DiscoveredInput>(StringComparer.Ordinal);
        var inDeckLink = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripLogPrefix(raw.TrimEnd('\r'));
            if (line.TrimStart().StartsWith("Auto-detected sources for ", StringComparison.OrdinalIgnoreCase))
            { inDeckLink = line.Trim().Equals("Auto-detected sources for decklink:", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inDeckLink || line.Length == 0 || (!char.IsWhiteSpace(line[0]) && line[0] != '*')) continue;
            var entry = Regex.Match(line, @"^\s*\*?\s*(?<body>.+)\s+\([^()]*\)\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (!entry.Success) continue;
            var body = entry.Groups["body"].Value.TrimEnd();
            if (!body.EndsWith(']')) continue;
            // Find the description's matching bracket from the right. Names and descriptions may contain balanced brackets.
            var depth = 0;
            var descriptionStart = -1;
            for (var index = body.Length - 1; index >= 0; index--)
            {
                if (body[index] == ']') depth++;
                else if (body[index] == '[' && --depth == 0) { descriptionStart = index; break; }
            }
            if (descriptionStart < 1 || !char.IsWhiteSpace(body[descriptionStart - 1])) continue;
            var name = body[..descriptionStart].Trim();
            if (!IsValidDeviceName(name)) continue;
            var description = body[(descriptionStart + 1)..^1];
            names.TryAdd(name, new(name, description.Length <= 512 && !description.Any(char.IsControl) ? description : null));
            if (names.Count > 256) throw new InvalidDataException("DeckLink source listing exceeds the 256-device safety limit.");
        }
        return names.Values.ToArray();
    }

    internal static DiscoveredInputMode[] ParseFormats(string text)
    {
        var modes = new Dictionary<string, DiscoveredInputMode>(StringComparer.Ordinal);
        var inFormats = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripLogPrefix(raw.TrimEnd('\r'));
            if (line.Contains("Supported formats for", StringComparison.OrdinalIgnoreCase)) { inFormats = true; continue; }
            if (!inFormats) continue;
            var entry = Regex.Match(line, @"^\s+(?<code>[A-Za-z0-9 ]{3,4})\s+(?<label>\d+x\d+\s+at\s+\d+/\d+\s+fps(?:\s+\([^\r\n]*\))?)\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (!entry.Success) continue;
            var code = entry.Groups["code"].Value.TrimEnd(); // FFmpeg accepts the FourCC 'pal ' as 'pal'.
            modes.TryAdd(code, new(code, entry.Groups["label"].Value.Trim()));
            if (modes.Count > 512) throw new InvalidDataException("DeckLink format listing exceeds the 512-mode safety limit.");
        }
        return modes.Values.ToArray();
    }

    public static void ValidateDeviceName(string? name)
    { if (!IsValidDeviceName(name)) throw new ArgumentException("Select a discovered device name of 1–512 characters without control characters."); }
    private static bool IsValidDeviceName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 512 && !name.Any(char.IsControl);
    private static string StripLogPrefix(string line) => Regex.Replace(line, @"^\[[^\]\r\n]*@[^\]\r\n]*\] ?", "", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static string Combine(DiscoveryProcessResult result) => result.StandardOutput + "\n" + result.StandardError;
    private static bool HasHardwareFailure(string text) => Regex.IsMatch(text,
        @"Cannot list sources|Could not (?:create|find|get|open)|Failed to (?:create|open|load)|DeckLink drivers (?:not installed|could not)|no (?:such )?device|access (?:is )?denied",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static string FailureDetail(DiscoveryProcessResult result)
    {
        var lines = Combine(result).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(8);
        var detail = string.Join(" ", lines);
        return $"FFmpeg exit {result.ExitCode}. " + (detail.Length == 0 ? "No diagnostic detail was returned." : Compact(detail));
    }
    private static string Compact(string value) => value.Length <= 1800 ? value : value[..1800] + "…";

    internal static async Task<DiscoveryProcessResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(MediaToolPaths.Resolve(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["AV_LOG_FORCE_NOCOLOR"] = "1";
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The configured FFmpeg executable could not be started.");
        ChildProcessLifetime? containment;
        try { containment = ChildProcessLifetime.Attach(process); }
        catch { Kill(process); await process.WaitForExitAsync(CancellationToken.None); throw; }
        using var lifetime = containment;
        using var registration = token.Register(() => Kill(process));
        async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var result = new StringBuilder();
            var buffer = new char[2048];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
                {
                    if (result.Length + count > MaximumOutputCharacters) throw new InvalidDataException("FFmpeg discovery output exceeds its bounded capture limit.");
                    result.Append(buffer, 0, count);
                }
                return result.ToString();
            }
            catch { Kill(process); throw; }
        }
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token);
            var output = await stdout;
            var error = await stderr;
            return new(process.ExitCode, output, error);
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (Exception) { /* Keep the original discovery/cancellation failure. */ }
            throw;
        }
    }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
}
