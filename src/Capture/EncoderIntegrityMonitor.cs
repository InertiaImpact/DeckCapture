using System.Text.RegularExpressions;

namespace DeckCapture.Capture;

// These are sticky reports from the owned FFmpeg process, not measured device counters.
// In particular, "Last message repeated N times" is not attributable to a specific
// warning here and must never become an invented lost-packet/frame total.
internal sealed class EncoderIntegrityMonitor
{
    private static readonly Regex Overrun = new(@"^\[(?:in#\d+/)?decklink @ [^\]\r\n]+\]\s+Decklink input buffer overrun!\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Duplication = new(@"^\[(?:vf#0:\d+|vost#0:\d+/[^\]\s]+) @ [^\]\r\n]+\]\s+More than \d+ frames duplicated\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private int overrun;
    private int duplicated;
    public bool InputBufferOverrun => Volatile.Read(ref overrun) != 0;
    public bool OutputFrameDuplication => Volatile.Read(ref duplicated) != 0;
    public string? Warning => (InputBufferOverrun, OutputFrameDuplication) switch
    {
        (true, true) => "FFmpeg reported DeckLink input buffer overruns and duplicated output frames. Source loss must be reviewed; exact loss counts are unavailable.",
        (true, false) => "FFmpeg reported a DeckLink input buffer overrun. Input packets were lost; an exact lost-frame count is unavailable.",
        (false, true) => "FFmpeg reported duplicated output frames. Review capture timing and source continuity.",
        _ => null
    };
    public void Observe(string line)
    {
        if (Overrun.IsMatch(line)) Volatile.Write(ref overrun, 1);
        if (Duplication.IsMatch(line)) Volatile.Write(ref duplicated, 1);
    }
    public void ObserveDuplicatedFrames(long count) { if (count > 0) Volatile.Write(ref duplicated, 1); }
}
