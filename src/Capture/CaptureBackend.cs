using System.Diagnostics;
using System.Text.Json;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

public sealed class CaptureBackend : ICaptureBackend
{
    public async Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var version = await MediaProcess.ReadAsync(profile.FfmpegPath, ["-version"], timeout.Token);
            var devices = await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-devices"], timeout.Token);
            // FFmpeg prints its device table to stdout. Do not infer DeckLink from the executable name.
            var deckLink = devices.Split('\n').Any(line => line.Contains("decklink", StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith('D'));
            await MediaProcess.ReadAsync(profile.FfprobePath, ["-version"], timeout.Token);
            return new(true, deckLink, version.Split('\n')[0].Trim(), deckLink ? null : "This FFmpeg build has no DeckLink input; synthetic sources only.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        { return new(false, false, "Unavailable", exception.Message); }
    }

    public async Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken)
    {
        var capabilities = await ProbeAsync(options.Profile, cancellationToken);
        if (!capabilities.Available || (!options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase) && !capabilities.DeckLinkSupported))
            throw new InvalidOperationException(capabilities.Error ?? "Required capture capability unavailable.");
        // Cache codec/runtime checks in the long-lived supervisor process, not in one-shot workers.
        var preview = await LivePreview.PrepareAsync(options.Profile, options.WorkingDirectory, options.Channel.AudioChannels,
            options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase) && options.Channel.AudioChannels > 2, cancellationToken);
        options = options with { LivePreviewPreflightCompleted = true, LivePreviewEncoderName = preview.Encoder?.Name, LivePreviewPreflightError = preview.Error,
            LivePreviewPreflightDiagnostics = preview.Diagnostics };
        await using (var preflightLog = new WorkerLogTail(Path.Combine(options.WorkingDirectory, "encoder.log")))
            foreach (var diagnostic in preview.Diagnostics) preflightLog.Append(diagnostic);
        cancellationToken.ThrowIfCancellationRequested();
        var session = new CaptureSession(options);
        try { await session.Ready.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); return session; }
        catch { await session.DisposeAsync(); throw; }
    }

    public static string ResolveWorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("DECKCAPTURE_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        if (ResolveBundledWorkerPath(AppContext.BaseDirectory, OperatingSystem.IsWindows()) is { } bundled) return bundled;
        // Development fallback works for dotnet run and test output, regardless of cwd.
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(directory.FullName, "src", "CaptureWorker", "bin", configuration, "net10.0", "DeckCapture.CaptureWorker.dll");
                    if (File.Exists(candidate)) return candidate;
                }
        }
        throw new FileNotFoundException("Capture worker missing. Build src/CaptureWorker or deploy capture-worker beside the supervisor; DECKCAPTURE_WORKER_PATH can override its location.");
    }

    internal static string? ResolveBundledWorkerPath(string baseDirectory, bool windows)
    {
        foreach (var directory in new[] { Path.Combine(baseDirectory, "capture-worker"), baseDirectory })
        {
            var executable = Path.Combine(directory, "DeckCapture.CaptureWorker.exe");
            // A framework-dependent apphost also ends in .exe. Prefer it only when the
            // worker carries its own runtime, so portable installs never fall back to machine dotnet.
            if (windows && File.Exists(executable) && File.Exists(Path.Combine(directory, "coreclr.dll"))) return executable;
            var assembly = Path.Combine(directory, "DeckCapture.CaptureWorker.dll");
            if (File.Exists(assembly)) return assembly;
        }
        return null;
    }
}

public sealed record WorkerCommand(string Kind, string Id, string? OutputPath = null, int DurationSeconds = 0, int PreRollSeconds = 0);
public sealed record WorkerEvent(string Kind, CaptureMetrics? Metrics = null, CaptureResult? Result = null, string? Id = null, string? Error = null);

internal sealed class CaptureSession : ICaptureSession
{
    private readonly MediaProcess worker;
    private readonly CaptureWorkerLifecycle lifecycle = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> pending = new();
    private readonly SemaphoreSlim writes = new(1);
    private readonly WorkerLogTail stdoutLog;
    private readonly WorkerLogTail stderrLog;
    private long lastLoggedMetrics;
    private CaptureMetricsObservation metrics = new(new(), 0);
    private bool disposed;
    public Task Ready => lifecycle.Ready;
    public CaptureMetrics Metrics => Volatile.Read(ref metrics).Current(Stopwatch.GetTimestamp());
    public Task<CaptureResult> Completion => lifecycle.Completion;

    public CaptureSession(CaptureOpenOptions options)
    {
        Directory.CreateDirectory(options.WorkingDirectory);
        var optionsPath = Path.Combine(options.WorkingDirectory, $"worker-{Guid.NewGuid():N}.json");
        File.WriteAllText(optionsPath, JsonSerializer.Serialize(options));
        var path = CaptureBackend.ResolveWorkerPath();
        var executable = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path;
        stdoutLog = new(Path.Combine(options.WorkingDirectory, "worker-stdout.log"));
        stderrLog = new(Path.Combine(options.WorkingDirectory, "worker-stderr.log"));
        try { worker = new MediaProcess(executable, executable == "dotnet" ? [path, optionsPath] : [optionsPath], Receive, line => stderrLog.Append(line)); }
        catch
        {
            stdoutLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            stderrLog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        _ = WatchAsync();
    }

    private void Receive(string line)
    {
        try
        {
            var update = JsonSerializer.Deserialize<WorkerEvent>(line);
            if (update is not null) LogProtocol(update);
            if (update?.Metrics is { } value) Volatile.Write(ref metrics, new(value, Stopwatch.GetTimestamp()));
            if (update is not null) lifecycle.Receive(update, stderrLog.RecentText());
            if (update?.Id is { } id && pending.TryRemove(id, out var waiter))
            { if (update.Error is null) waiter.TrySetResult(); else waiter.TrySetException(new IOException(update.Error)); }
        }
        catch (JsonException) { stdoutLog.Append("Invalid worker protocol line omitted."); lifecycle.InvalidProtocol(); }
    }

    private void LogProtocol(WorkerEvent update)
    {
        if (update.Kind == "metrics")
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (lastLoggedMetrics != 0 && Stopwatch.GetElapsedTime(lastLoggedMetrics, timestamp) < TimeSpan.FromSeconds(30)) return;
            lastLoggedMetrics = timestamp;
        }
        var sanitized = update with
        {
            Error = update.Error is null ? null : WorkerLogTail.Sanitize(update.Error),
            Metrics = update.Metrics is null ? null : update.Metrics with { PreviewPath = null, LivePreviewPath = null, LumaWaveform = [], Warning = update.Metrics.Warning is null ? null : WorkerLogTail.Sanitize(update.Metrics.Warning) },
            Result = update.Result is null ? null : update.Result with { MediaPath = null, Error = update.Result.Error is null ? null : WorkerLogTail.Sanitize(update.Result.Error) }
        };
        stdoutLog.Append(JsonSerializer.Serialize(sanitized), alreadySanitized: true);
    }

    private async Task WatchAsync()
    {
        try
        {
            var code = await worker.WaitAsync(CancellationToken.None);
            var exception = lifecycle.Exited(code, Metrics, stderrLog.RecentText());
            foreach (var waiter in pending.Values) waiter.TrySetException(exception);
        }
        catch (Exception exception)
        {
            var failure = lifecycle.Faulted(exception, Metrics);
            foreach (var waiter in pending.Values) waiter.TrySetException(failure);
        }
    }

    private async Task SendAsync(WorkerCommand command, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[command.Id] = waiter;
        try
        {
            await writes.WaitAsync(token);
            try { await worker.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)); await worker.Process.StandardInput.FlushAsync(token); }
            finally { writes.Release(); }
            await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally { pending.TryRemove(command.Id, out _); }
    }

    public Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken)
        => SendAsync(new("begin", Guid.NewGuid().ToString("N"), outputPath, durationSeconds, preRollSeconds), cancellationToken);
    public Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken)
        => SendAsync(new("duration", Guid.NewGuid().ToString("N"), DurationSeconds: totalDurationSeconds), cancellationToken);
    public async Task<CaptureResult> StopAsync(CancellationToken cancellationToken)
    {
        if (!Completion.IsCompleted)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await writes.WaitAsync(cancellationToken);
            try
            {
                if (!Completion.IsCompleted)
                {
                    // A timed stop may already be finalizing. The worker need not read/acknowledge
                    // another stop command before exiting; its Completion result is authoritative.
                    try
                    {
                        await worker.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new WorkerCommand("stop", Guid.NewGuid().ToString("N"))));
                        await worker.Process.StandardInput.FlushAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException)
                    {
                        // The worker may have closed its pipe immediately after publishing its result.
                        // WatchAsync drains stdout before supplying an exit failure if no result arrived.
                    }
                }
            }
            finally { writes.Release(); }
        }
        return await Completion.WaitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await StopAsync(timeout.Token); }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException) { }
        disposed = true;
        try { await worker.DisposeAsync(); }
        finally { await stdoutLog.DisposeAsync(); await stderrLog.DisposeAsync(); }
        writes.Dispose();
    }
}
