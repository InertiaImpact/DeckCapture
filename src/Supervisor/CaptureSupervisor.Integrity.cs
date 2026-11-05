using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed partial class CaptureSupervisor
{
    private void RecordIntegrityWarningsUnderGate(RecordingJob job, CaptureMetrics metrics)
    {
        var warnings = new List<string>();
        if (metrics.InputBufferOverrun) warnings.Add("FFmpeg reported a DeckLink input buffer overrun: input packet loss; exact frame count unavailable.");
        if (metrics.OutputFrameDuplication) warnings.Add("FFmpeg reported duplicated output frames; review capture timing and continuity.");
        var changed = false;
        foreach (var warning in warnings)
        {
            if (job.Warnings.Contains(warning, StringComparer.Ordinal)) continue;
            job.Warnings.Add(warning); changed = true; Alert("Error", $"{job.FileName}: {warning}");
        }
        if (changed) Save(job);
    }
}
