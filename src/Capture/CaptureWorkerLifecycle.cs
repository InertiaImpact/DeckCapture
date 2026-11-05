using DeckCapture.Domain;

namespace DeckCapture.Capture;

// Startup readiness and capture completion are separate promises. A final result cannot
// leave startup pending, and a process exit must never replace an explicit final result.
internal sealed class CaptureWorkerLifecycle
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CaptureResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public Task<CaptureResult> Completion => completion.Task;

    public void Receive(WorkerEvent update, string stderrTail)
    {
        if (update.Kind == "ready") ready.TrySetResult();
        if (update.Result is not { } result) return;
        var safe = result with { Error = CleanOptional(result.Error) };
        completion.TrySetResult(safe);
        if (ready.Task.IsCompleted) return;
        var detail = safe.Error ?? CleanOptional(update.Error) ?? CleanOptional(stderrTail);
        var message = result.Success
            ? "Capture worker completed before its input became ready. Prepare the input again."
            : "Capture input failed before becoming ready: " + (detail ?? "The worker reported failure without diagnostic detail.");
        ready.TrySetException(new IOException(message));
    }

    // Called only after MediaProcess.WaitAsync has drained both protocol and diagnostic pipes.
    public IOException Exited(int code, CaptureMetrics metrics, string stderrTail)
    {
        var explicitError = completion.Task.IsCompletedSuccessfully ? CleanOptional(completion.Task.Result.Error) : null;
        var tail = CleanOptional(stderrTail);
        var exception = new IOException(explicitError ?? $"Capture worker exited ({code})" + (tail is null ? ". No diagnostic detail was returned." : ": " + tail));
        ready.TrySetException(exception);
        completion.TrySetResult(new(false, null, metrics.MediaSeconds, metrics.Frames, metrics.DroppedFrames, exception.Message));
        return exception;
    }

    public IOException Faulted(Exception exception, CaptureMetrics metrics)
    {
        var failure = new IOException("Capture worker monitoring failed: " + (CleanOptional(exception.Message) ?? "No diagnostic detail was returned."));
        ready.TrySetException(failure);
        completion.TrySetResult(new(false, null, metrics.MediaSeconds, metrics.Frames, metrics.DroppedFrames, failure.Message));
        return failure;
    }

    public void InvalidProtocol() => ready.TrySetException(new IOException("Invalid worker protocol."));

    internal static string? CleanOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = WorkerLogTail.Sanitize(value.Trim());
        return clean.Length <= 2048 ? clean : clean[..2047] + "…";
    }
}
