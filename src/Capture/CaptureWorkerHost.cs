using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

/// <summary>Owns the sole live input. Monitoring reads closed spool files, never the device.</summary>
public static class CaptureWorkerHost
{
    public static async Task<int> RunAsync(CaptureOpenOptions options)
    {
        await using var engine = new SpoolCapture(options);
        var commands = Channel.CreateBounded<WorkerCommand>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    var command = JsonSerializer.Deserialize<WorkerCommand>(line);
                    if (command is not null) await commands.Writer.WriteAsync(command);
                }
            }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); }
            finally { commands.Writer.TryComplete(); }
        });
        try
        {
            await engine.StartAsync();
            var readySent = false;
            while (true)
            {
                while (commands.Reader.TryRead(out var command))
                {
                    try
                    {
                        switch (command.Kind)
                        {
                            case "begin": engine.Begin(command); break;
                            case "duration": engine.UpdateDuration(command.DurationSeconds); break;
                            case "stop": engine.RequestStop(); break;
                            default: throw new InvalidOperationException("Unknown worker command.");
                        }
                        Emit(new("ack", Id: command.Id));
                    }
                    catch (Exception exception) { Emit(new("ack", Id: command.Id, Error: exception.Message)); }
                }
                if (commands.Reader.Completion.IsCompleted) engine.RequestStop();
                await engine.TickAsync();
                if (!readySent && engine.HasClosedSegment && engine.Metrics.PreviewPath is not null) { readySent = true; Emit(new("ready", Metrics: engine.Metrics)); }
                Emit(new("metrics", Metrics: engine.Metrics));
                if (engine.ShouldStop)
                {
                    var result = await engine.FinishAsync();
                    Emit(new("complete", Result: result, Metrics: engine.Metrics));
                    return result.Success ? 0 : 1;
                }
                await Task.Delay(200);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Emit(new("complete", Result: new(false, null, engine.Metrics.MediaSeconds, engine.Metrics.Frames,
                engine.Metrics.DroppedFrames, exception.Message)));
            return 1;
        }
    }

    private static void Emit(WorkerEvent message) => Console.Out.WriteLine(JsonSerializer.Serialize(message));
}

internal sealed class SpoolCapture : IAsyncDisposable
{
    private readonly CaptureOpenOptions options;
    private readonly string directory;
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private readonly Stopwatch recording = new();
    private readonly DeckLinkSignalMonitor inputSignal = new();
    private readonly EncoderIntegrityMonitor integrity = new();
    private readonly SegmentMonitor segmentMonitor = new();
    private readonly WorkerLogTail encoderLog;
    private MediaProcess? encoder;
    private string? output;
    private string? firstRecordingSegment;
    private string? lastSample;
    private Task<CaptureMetrics>? monitorTask;
    private readonly Dictionary<string, double> segmentDurations = new(StringComparer.Ordinal);
    private int duration;
    private double actualPreRoll;
    private double closedPreRoll;
    private double? requestedStopSeconds;
    private bool stop;
    private long frames;
    private long drops;
    private double mediaSeconds;
    private long? recordingStartFrames;
    private double recordingStartMediaSeconds;
    private long progressTimestamp;
    private long monitorRequestedTimestamp;
    private long analysisTimestamp;
    private CaptureMetrics metrics = new();
    private LivePreviewEncoder? livePreviewEncoder;
    private string? livePreviewError;
    private DateTime previewWriteTime;
    private long previewChangedAt;
    private bool livePreviewAvailable;
    public CaptureMetrics Metrics
    {
        get
        {
            var alive = encoder is not null && !encoder.Process.HasExited;
            var synthetic = options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase);
            var presence = alive ? synthetic ? true : inputSignal.InputSignalPresent : null;
            return metrics with
            {
                Frames = Interlocked.Read(ref frames), DroppedFrames = Interlocked.Read(ref drops), MediaSeconds = Volatile.Read(ref mediaSeconds),
                RecordingFrames = recordingStartFrames is { } baseline ? Math.Max(0, Interlocked.Read(ref frames) - baseline) : null,
                RecordingMediaSeconds = recordingStartFrames.HasValue ? Math.Max(0, Volatile.Read(ref mediaSeconds) - recordingStartMediaSeconds) : null,
                SignalLocked = alive ? metrics.SignalLocked : null, InputSignalPresent = presence,
                InputSignalStatusSource = synthetic && alive ? "Synthetic" : presence.HasValue ? "FfmpegDeckLinkFrameFlag" : "Unavailable",
                DetectedVideoFormat = alive && presence != false ? metrics.DetectedVideoFormat : null,
                AnalysisAvailable = alive && metrics.AnalysisAvailable,
                InputBufferOverrun = integrity.InputBufferOverrun, OutputFrameDuplication = integrity.OutputFrameDuplication,
                IntegrityWarning = integrity.Warning,
                LivePreviewPath = livePreviewEncoder is null ? null : Path.Combine(directory, LivePreview.DirectoryName, LivePreview.PlaylistName),
                LivePreviewAvailable = alive && livePreviewAvailable, LivePreviewError = livePreviewError,
                AnalysisAgeSeconds = analysisTimestamp == 0 ? null : Stopwatch.GetElapsedTime(analysisTimestamp).TotalSeconds
            };
        }
    }
    public bool HasClosedSegment => ClosedSegments().Length > 0;
    public bool ShouldStop => stop || encoder?.Process.HasExited == true || (recording.IsRunning && recording.Elapsed.TotalSeconds >= duration)
        || MonitoringExpired;
    private bool MonitoringExpired => IsMonitoringExpired(options, output is not null, lifetime.Elapsed);

    internal static bool IsMonitoringExpired(CaptureOpenOptions options, bool recordingStarted, TimeSpan elapsed)
    {
        // Manual monitoring is unlimited by default; recording preparation retains its separate limit.
        // Neither monitoring policy can stop a recording, whose own duration timer is authoritative.
        if (recordingStarted) return false;
        var minutes = options.MonitorOnly ? options.Profile.MonitoringTimeoutMinutes : options.Profile.MaximumArmedMinutes;
        return minutes > 0 && elapsed.TotalMinutes >= minutes;
    }

    internal static string MonitoringExpiryMessage(CaptureOpenOptions options) => options.MonitorOnly
        ? $"Monitoring expired after its configured {options.Profile.MonitoringTimeoutMinutes}-minute timeout. Start monitoring again when needed. Original spool retained for inspection."
        : $"Monitoring expired after {options.Profile.MaximumArmedMinutes} armed minutes without recording. Prepare a new job; original spool retained for inspection.";

    public SpoolCapture(CaptureOpenOptions options)
    {
        this.options = options;
        directory = Path.Combine(Path.GetFullPath(options.WorkingDirectory), "spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Fixed parent evidence name is discoverable remotely without exposing the random spool path.
        encoderLog = new(Path.Combine(options.WorkingDirectory, "encoder.log"));
    }

    public async Task StartAsync()
    {
        var preview = options.LivePreviewPreflightCompleted
            ? new LivePreviewPreparation(options.LivePreviewEncoderName is "h264_mf" or "libx264" ? new(options.LivePreviewEncoderName) : null, options.LivePreviewPreflightError)
                { Diagnostics = options.LivePreviewPreflightDiagnostics }
            : await LivePreview.PrepareAsync(options.Profile, directory, options.Channel.AudioChannels,
                options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase) && options.Channel.AudioChannels > 2);
        livePreviewEncoder = preview.Encoder; livePreviewError = preview.Error;
        foreach (var diagnostic in preview.Diagnostics) encoderLog.Append(diagnostic);
        if (livePreviewEncoder is not null) Directory.CreateDirectory(Path.Combine(directory, LivePreview.DirectoryName));
        encoder = new MediaProcess(options.Profile.FfmpegPath, BuildEncoderArguments(options, directory, livePreviewEncoder), ReadProgress, ReadEncoderDiagnostic);
    }

    internal static IReadOnlyList<string> BuildEncoderArguments(CaptureOpenOptions options, string directory, LivePreviewEncoder? preview = null)
    {
        ValidateOptions(options);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-filter_threads", "1", "-stats_period", "1", "-progress", "pipe:1" };
        if (options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            var profile = options.Profile;
            var pattern = options.Channel.SyntheticPattern.Equals("black", StringComparison.OrdinalIgnoreCase)
                ? $"color=c=black:s={profile.SyntheticWidth}x{profile.SyntheticHeight}:r={profile.SyntheticFramesPerSecond}"
                : $"testsrc2=size={profile.SyntheticWidth}x{profile.SyntheticHeight}:rate={profile.SyntheticFramesPerSecond}";
            var audio = options.Channel.SyntheticPattern.Equals("black", StringComparison.OrdinalIgnoreCase)
                ? "anullsrc=r=48000:cl=stereo" : "sine=frequency=1000:sample_rate=48000";
            args.AddRange(["-re", "-f", "lavfi", "-i", pattern, "-re", "-f", "lavfi", "-i", audio, "-map", "0:v:0", "-map", "1:a:0"]);
        }
        else
        {
            // DeckLink defaults to 16-bit audio. Request its 32-bit input container before
            // opening the device; converting that input to PCM24 does not establish source valid bits.
            args.AddRange(["-f", "decklink", "-raw_format", "yuv422p10", "-audio_depth", "32"]);
            // Omitting format_code invokes FFmpeg's SDK autodetection on this owned input.
            // It requires supported hardware and an initial signal; it is not continuous mode following.
            if (!CaptureFormatCode.IsAutomatic(options.Channel.FormatCode)) args.AddRange(["-format_code", options.Channel.FormatCode]);
            args.AddRange(["-channels", options.Channel.AudioChannels.ToString(CultureInfo.InvariantCulture), "-i", options.Channel.DeviceName, "-map", "0:v:0", "-map", "0:a:0"]);
        }
        // FFV1 intra frames make each segment independently decodable. No resampling/scaling of DeckLink video.
        args.AddRange(["-c:v", "ffv1", "-threads:v", options.Profile.EncoderThreads.ToString(CultureInfo.InvariantCulture), "-level", "3", "-coder", "1", "-context", "1", "-g", "1", "-slicecrc", "1", "-pix_fmt", "yuv422p10le",
            "-fps_mode:v", "passthrough",
            "-c:a", "pcm_s24le", "-ar", "48000", "-ac", options.Channel.AudioChannels.ToString(CultureInfo.InvariantCulture),
            "-f", "segment", "-segment_time", "1", "-reset_timestamps", "1", "-segment_list", Path.Combine(directory, "segments.csv"),
            "-segment_list_type", "csv", "-segment_list_size", "3", "-segment_list_flags", "+live", Path.Combine(directory, "segment-%09d.mkv")]);
        // The first output remains preservation. Preview maps the already-owned input and
        // applies its frame-rate/size/pixel conversion only to the second output.
        if (preview is not null && options.Profile.LivePreviewEnabled) LivePreview.AppendOutput(args, preview, Path.Combine(directory, LivePreview.DirectoryName), "0:v:0",
            options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase) ? "1:a:0" : "0:a:0",
            firstPair: options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase) && options.Channel.AudioChannels > 2);
        return args;
    }

    public static void ValidateOptions(CaptureOpenOptions options)
    {
        var profile = options.Profile;
        if (profile.PreRollSeconds is < 0 or > 60 || profile.MaximumArmedMinutes is < 1 or > 240
            || profile.MonitoringTimeoutMinutes is < 0 or > 1440
            || profile.EncoderThreads is < 1 or > 64
            || profile.SyntheticWidth is < 64 or > 4096 || profile.SyntheticHeight is < 64 or > 2160
            || profile.SyntheticFramesPerSecond is < 1 or > 60) throw new ArgumentException("Capture profile is outside supported development bounds.");
        if (options.Channel.AudioChannels is not (2 or 8 or 16)) throw new ArgumentException("Audio channels must be 2, 8, or 16.");
        if (!options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase) && !options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SourceKind must be Synthetic or DeckLink.");
        if (options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase) && options.Channel.SyntheticPattern is not ("testsrc2" or "black"))
            throw new ArgumentException("SyntheticPattern must be testsrc2 or black.");
        if (options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(options.Channel.DeviceName))
            throw new ArgumentException("DeckLink device name is required.");
        if (options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase) && !CaptureFormatCode.IsValid(options.Channel.FormatCode))
            throw new ArgumentException("DeckLink format must be auto or a three/four-character format code.");
    }

    internal void ReadProgress(string line)
    {
        var fields = line.Split('=', 2);
        if (fields.Length != 2) return;
        if (fields[0] == "frame" && long.TryParse(fields[1], out var count)) Interlocked.Exchange(ref frames, count);
        if (fields[0] == "drop_frames" && long.TryParse(fields[1], out count)) Interlocked.Exchange(ref drops, count);
        if (fields[0] == "dup_frames" && long.TryParse(fields[1], out count)) integrity.ObserveDuplicatedFrames(count);
        if (fields[0] == "out_time_us" && long.TryParse(fields[1], out count))
        { Volatile.Write(ref mediaSeconds, count / 1_000_000d); Interlocked.Exchange(ref progressTimestamp, Stopwatch.GetTimestamp()); }
    }

    private void ReadEncoderDiagnostic(string line)
    {
        if (options.Channel.SourceKind.Equals("DeckLink", StringComparison.OrdinalIgnoreCase)) inputSignal.Observe(line);
        integrity.Observe(line);
        // Keep a bounded live tail rather than waiting for finalization to expose input warnings.
        // Disk persistence is best-effort and never blocks FFmpeg's stderr reader.
        encoderLog.Append(line);
    }

    private string[] Segments() => Directory.GetFiles(directory, "segment-*.mkv").Order(StringComparer.Ordinal).ToArray();
    private string[] ClosedSegments()
    {
        // FFmpeg publishes at most three closed entries. Never scan an hours-long recording on each monitoring tick.
        try
        {
            var list = Path.Combine(directory, "segments.csv");
            if (!File.Exists(list)) return [];
            var paths = new List<string>();
            foreach (var line in File.ReadAllLines(list))
            {
                var fields = line.Split(',');
                var name = fields[0].Trim('"');
                if (fields.Length != 3 || !name.StartsWith("segment-", StringComparison.Ordinal) || Path.GetFileName(name) != name || !name.EndsWith(".mkv", StringComparison.Ordinal)) continue;
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                if (double.TryParse(fields[1], CultureInfo.InvariantCulture, out var start) && double.TryParse(fields[2], CultureInfo.InvariantCulture, out var end) && end > start)
                    segmentDurations[path] = end - start;
                paths.Add(path);
            }
            return paths.Order(StringComparer.Ordinal).ToArray();
        }
        catch (IOException) { return []; } // A concurrent list replacement is retried on the next 200 ms tick.
    }

    public void Begin(WorkerCommand command)
    {
        if (recording.IsRunning || output is not null) throw new InvalidOperationException("Session already recording.");
        if (command.DurationSeconds is < 1 or > 86400 || command.PreRollSeconds < 0 || command.PreRollSeconds > options.Profile.PreRollSeconds)
            throw new ArgumentException("Invalid duration or pre-roll.");
        var path = Path.GetFullPath(command.OutputPath ?? throw new ArgumentException("Output path required."));
        if (File.Exists(path) || File.Exists(path + ".partial")) throw new IOException("Recording output already exists; never overwrite it.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var segments = Segments();
        if (segments.Length == 0) throw new IOException("No input media is available yet.");
        // Retain ceil(pre-roll) closed one-second segments plus the currently open segment.
        // The open segment contributes up to one additional second: measured and exposed in timing.json.
        var selected = Math.Max(0, segments.Length - 1 - command.PreRollSeconds);
        firstRecordingSegment = segments[selected];
        closedPreRoll = segments.Length - 1 - selected;
        var segmentIndex = int.Parse(Path.GetFileNameWithoutExtension(firstRecordingSegment)[8..], CultureInfo.InvariantCulture);
        var timestamp = Interlocked.Read(ref progressTimestamp);
        var estimatedSourceNow = Volatile.Read(ref mediaSeconds) + (timestamp == 0 ? 0 : Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
        actualPreRoll = Math.Clamp(estimatedSourceNow - segmentIndex, closedPreRoll, closedPreRoll + 1);
        duration = command.DurationSeconds;
        // UI-only deltas: retain input counters, dropped-frame evidence, timestamps and
        // final file validation unchanged. These sampled deltas exclude pre-roll.
        recordingStartFrames = Interlocked.Read(ref frames);
        recordingStartMediaSeconds = Volatile.Read(ref mediaSeconds);
        output = path;
        recording.Start();
        SaveTiming("recording");
    }

    public void UpdateDuration(int seconds)
    {
        if (!recording.IsRunning || stop) throw new InvalidOperationException("No active recording to extend.");
        if (seconds is < 1 or > 86400 || seconds < duration) throw new ArgumentException("Duration may only increase, up to 24 hours.");
        duration = seconds;
        SaveTiming("recording");
    }
    public void RequestStop()
    {
        if (!stop && recording.IsRunning) requestedStopSeconds = recording.Elapsed.TotalSeconds;
        stop = true;
    }

    public async Task TickAsync()
    {
        RefreshLivePreview();
        var closed = ClosedSegments();
        if (closed.Length == 0) return;
        var newest = closed[^1];
        if (monitorTask?.IsCompleted == true)
        {
            try { metrics = await monitorTask; analysisTimestamp = monitorRequestedTimestamp; }
            catch (Exception exception) { metrics = metrics with { Warning = "Monitoring failed: " + exception.Message, AnalysisAvailable = false }; }
            monitorTask = null;
        }
        if (newest != lastSample && monitorTask is null)
        {
            lastSample = newest;
            monitorRequestedTimestamp = Stopwatch.GetTimestamp();
            monitorTask = segmentMonitor.AnalyzeAsync(options.Profile, newest, directory, options.Channel.SourceKind.Equals("Synthetic", StringComparison.OrdinalIgnoreCase));
        }
        // While armed, the spool itself is bounded, so this enumeration remains bounded as well.
        var allClosed = output is null ? Segments().Where(path => string.CompareOrdinal(path, newest) <= 0).ToArray() : closed;
        var retain = output is null ? allClosed.Take(Math.Max(0, allClosed.Length - options.Profile.PreRollSeconds - 1))
            : closed.Where(path => string.CompareOrdinal(path, firstRecordingSegment) < 0);
        foreach (var path in retain)
            if (path != lastSample || monitorTask is null) { File.Delete(path); segmentDurations.Remove(path); }
    }

    private void RefreshLivePreview()
    {
        if (livePreviewEncoder is null) return;
        try
        {
            var playlist = new FileInfo(Path.Combine(directory, LivePreview.DirectoryName, LivePreview.PlaylistName));
            if (!playlist.Exists) { livePreviewAvailable = false; livePreviewError = "Live A/V preview is starting."; return; }
            if (playlist.LastWriteTimeUtc != previewWriteTime)
            { previewWriteTime = playlist.LastWriteTimeUtc; previewChangedAt = Stopwatch.GetTimestamp(); }
            livePreviewAvailable = previewChangedAt != 0 && Stopwatch.GetElapsedTime(previewChangedAt).TotalSeconds <= 5;
            livePreviewError = livePreviewAvailable ? null : "Live A/V preview has not updated recently.";
        }
        catch (IOException) { livePreviewAvailable = false; livePreviewError = "Live A/V preview is temporarily unavailable."; }
    }

    public async Task<CaptureResult> FinishAsync()
    {
        if (encoder is null) throw new InvalidOperationException("Encoder was not started.");
        var unexpectedExit = encoder.Process.HasExited;
        var expired = MonitoringExpired;
        await encoder.StopAsync();
        var encoderExitCode = encoder.Process.ExitCode;
        var encoderFailure = EncoderExitError(encoderExitCode, unexpectedExit, encoder.Error);
        encoderLog.Append($"Encoder process {(unexpectedExit ? "exited before stop" : "finished after stop request")}: {DescribeEncoderExit(encoderExitCode)}.");
        recording.Stop();
        _ = ClosedSegments(); // Collect the last, possibly short, segment's original time interval.
        await encoderLog.DisposeAsync();
        SaveTiming("finalizing");
        if (output is null) return new(false, null, 0, Metrics.Frames, Metrics.DroppedFrames,
            expired ? MonitoringExpiryMessage(options)
            : encoderFailure ?? "Monitoring stopped without a recording; prepare a new job. Original spool retained for inspection.");
        if (encoderFailure is not null)
            return new(false, null, 0, Metrics.Frames, Metrics.DroppedFrames, encoderFailure);
        var files = Segments().Where(path => string.CompareOrdinal(path, firstRecordingSegment) >= 0).ToArray();
        if (files.Length == 0) return new(false, null, 0, 0, 0, "No media segments were retained.");
        var manifest = Path.Combine(directory, "concat.txt");
        // Only worker-generated basename values enter concat syntax; arbitrary user paths never do.
        var manifestLines = new List<string>();
        foreach (var path in files)
        {
            manifestLines.Add("file '" + Path.GetFileName(path) + "'");
            // Container audio packet tails must not accumulate into seconds of concat drift.
            // Preserve the segment muxer's original source-time interval rather than container duration.
            if (segmentDurations.TryGetValue(path, out var interval)) manifestLines.Add("duration " + interval.ToString("F9", CultureInfo.InvariantCulture));
            else return new(false, null, 0, Metrics.Frames, Metrics.DroppedFrames, "Missing segment timing; original spool retained for review.");
        }
        await File.WriteAllLinesAsync(manifest, manifestLines);
        var partial = output + ".partial";
        var finalDurationCap = Math.Min(duration, requestedStopSeconds ?? duration) + actualPreRoll;
        var finalizationBudget = FinalizationTimeout(duration);
        var phase = "remuxing";
        try
        {
            using var timeout = new CancellationTokenSource(finalizationBudget);
            SaveTiming(phase);
            await MediaProcess.ReadAsync(options.Profile.FfmpegPath, ["-hide_banner", "-loglevel", "warning", "-nostdin", "-n", "-f", "concat", "-safe", "1", "-i", manifest,
                "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-t", finalDurationCap.ToString("F6", CultureInfo.InvariantCulture), "-f", "matroska", partial], timeout.Token);
            phase = "validating";
            SaveTiming(phase);
            var validation = await MediaValidation.ValidateAsync(options.Profile.FfprobePath, partial, timeout.Token);
            if (validation.AudioChannels != options.Channel.AudioChannels) throw new InvalidDataException("Audio channel count differs from the requested source profile.");
            await File.WriteAllTextAsync(Path.Combine(directory, "validation.json"), JsonSerializer.Serialize(validation));
            if (validation.DurationSeconds > finalDurationCap + 0.15) throw new InvalidDataException("Final media exceeds duration bound (including packet tolerance).");
            if (Metrics.DroppedFrames > 0) throw new InvalidDataException("FFmpeg reported dropped frames; recording requires review and remains partial.");
            File.Move(partial, output, overwrite: false);
            SaveTiming("complete", validation.DurationSeconds);
            return new(true, output, validation.DurationSeconds, validation.Frames, Metrics.DroppedFrames);
        }
        catch (OperationCanceledException)
        {
            // This deadline is owned by finalization, not by an operator cancel.
            return new(false, File.Exists(partial) ? partial : null, 0, Metrics.Frames, Metrics.DroppedFrames,
                $"Finalization timed out while {phase} after {finalizationBudget.TotalSeconds:F0} seconds; original segments and partial file retained.");
        }
        catch (Exception exception)
        {
            return new(false, File.Exists(partial) ? partial : null, 0, Metrics.Frames, Metrics.DroppedFrames, "Finalization failed; original segments retained: " + exception.Message);
        }
    }

    internal static string? EncoderExitError(int exitCode, bool unexpectedExit, string diagnostics)
    {
        // An OS fault during requested shutdown still means uncertain media; a valid last
        // segment or a harmless last stderr warning cannot authorize finalization success.
        if (!unexpectedExit && exitCode == 0) return null;
        return "Input encoder failed (" + DescribeEncoderExit(exitCode) + (unexpectedExit ? "; exited before stop request" : "; after stop request") +
            "); original spool retained." + (string.IsNullOrWhiteSpace(diagnostics) ? " No encoder diagnostic text was returned." : " Encoder diagnostics: " + diagnostics);
    }

    private static string DescribeEncoderExit(int code) => $"exit code {code} (0x{unchecked((uint)code):X8}" +
        (unchecked((uint)code) == 0xC0000005 ? ", access violation)" : ")");

    // Full count_frames validation decodes the master. Allow 0.5x realtime plus startup/remux
    // overhead for long tapes, while keeping failed/hung finalization bounded to one day.
    internal static TimeSpan FinalizationTimeout(int requestedDurationSeconds)
        => TimeSpan.FromSeconds(Math.Clamp(2L * requestedDurationSeconds + 300, 300, 86400));

    private void SaveTiming(string state, double? finalDuration = null) => File.WriteAllText(Path.Combine(directory, "timing.json"), JsonSerializer.Serialize(new
    {
        state, output, requestedSecondsAfterTrigger = duration, retainedClosedPreRollSeconds = closedPreRoll,
        estimatedAvailablePreRollSeconds = actualPreRoll,
        manualStopRequestedAfterTriggerSeconds = requestedStopSeconds,
        openSegmentAdditionalPreRollBoundSeconds = 1, segmentGranularitySeconds = 1,
        elapsedAfterBeginSeconds = recording.Elapsed.TotalSeconds, finalDurationSeconds = finalDuration,
        encoderExitCode = encoder?.Process.HasExited == true ? encoder.Process.ExitCode : (int?)null,
        note = "Start is segment-granular. Duration runs after trigger, pre-roll is additional. Pre-roll estimate interpolates FFmpeg progress with a monotonic clock; subsecond placement requires hardware acceptance. A/V packet trimming tolerance is 150 ms."
    }));

    public async ValueTask DisposeAsync()
    {
        if (encoder is not null) await encoder.DisposeAsync();
        await encoderLog.DisposeAsync();
        if (monitorTask is not null) { try { await monitorTask; } catch (Exception) { /* Monitor failures cannot prevent input cleanup. */ } }
    }
}
