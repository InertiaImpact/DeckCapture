using System.Diagnostics;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

// Worker ages continue advancing if its protocol stalls. Store value/time together so a
// reader cannot accidentally pair old analysis with a newly received timestamp.
internal sealed record CaptureMetricsObservation(CaptureMetrics Value, long ReceivedAt)
{
    public CaptureMetrics Current(long timestamp) => Value with
    { AnalysisAgeSeconds = ReceivedAt == 0 || Value.AnalysisAgeSeconds is not { } age ? null
        : age + Math.Max(0, Stopwatch.GetElapsedTime(ReceivedAt, timestamp).TotalSeconds) };
}
