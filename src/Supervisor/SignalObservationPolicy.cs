using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

internal static class SignalObservationPolicy
{
    // Samples come from completed segments, already about 1–2 s behind the source.
    // Unknown lock is different from an explicit negative input/lock observation.
    public static bool HasFreshAnalysis(CaptureMetrics metrics) => metrics.AnalysisAvailable &&
        metrics.AnalysisAgeSeconds is >= 0 and <= 5;
    public static bool CanAnalyzeContent(CaptureMetrics metrics) => HasFreshAnalysis(metrics) &&
        metrics.SignalLocked != false && metrics.InputSignalPresent != false;
    public static bool IsContent(CaptureMetrics metrics) => CanAnalyzeContent(metrics) && !metrics.IsBlack;
    public static string? LossWarning(CaptureMetrics metrics) => metrics.InputSignalPresent == false
        ? "FFmpeg reported no input signal on the capture input."
        : metrics.SignalLocked == false ? "The capture adapter reported that the input signal is not locked." : null;
}
