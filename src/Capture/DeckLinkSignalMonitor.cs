using System.Text.RegularExpressions;

namespace DeckCapture.Capture;

// FFmpeg 9 DeckLink logs these transitions from bmdFrameHasNoInputSource in its
// existing input callback. They establish reported input presence, not PLL lock,
// continuous per-frame validity, physical dropped-frame totals or device-loss counts.
internal sealed class DeckLinkSignalMonitor
{
    private static readonly Regex Transition = new(
        @"^\[(?:in#\d+/)?decklink @ [^\]\r\n]+\]\s+Frame received \(#\d+\) - (?<state>No input signal detected|Input returned) - Frames dropped \d+\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private int present = -1;
    public bool? InputSignalPresent => Volatile.Read(ref present) switch { 0 => false, 1 => true, _ => null };

    public void Observe(string line)
    {
        var match = Transition.Match(line);
        if (!match.Success) return;
        Volatile.Write(ref present, match.Groups["state"].Value == "Input returned" ? 1 : 0);
    }
}
