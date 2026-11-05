using System.Diagnostics;
using System.Text.RegularExpressions;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

internal sealed record LivePreviewEncoder(string Name);
internal sealed record LivePreviewPreparation(LivePreviewEncoder? Encoder, string? Error)
{
    public string[] Diagnostics { get; init; } = [];
}

/// <summary>Low-rate HLS output from the encoder's existing input. It never opens a hardware device.</summary>
internal static class LivePreview
{
    internal const string DirectoryName = "preview-hls";
    internal const string PlaylistName = "index.m3u8";
    private const string HlsOptions = "hls_time=1:hls_list_size=6:hls_delete_threshold=2:hls_flags=delete_segments+independent_segments+temp_file+omit_endlist+append_list";
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static readonly Dictionary<string, (long At, LivePreviewPreparation Result)> Cache = new(StringComparer.Ordinal);

    internal static async Task<LivePreviewPreparation> PrepareAsync(CaptureProfile profile, string spoolDirectory, int sourceAudioChannels = 2, bool firstPair = false, CancellationToken cancellationToken = default)
    {
        if (!profile.LivePreviewEnabled) return new(null, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var acquired = false;
        var diagnostics = new List<string>();
        try
        {
            await ProbeGate.WaitAsync(timeout.Token); acquired = true;
            var identity = ToolIdentity(profile.FfmpegPath);
            if (identity is not null) identity = CacheIdentity(identity, sourceAudioChannels, firstPair);
            if (identity is not null && Cache.TryGetValue(identity, out var cached) && Stopwatch.GetElapsedTime(cached.At).TotalMinutes < 10)
                return cached.Result;
            var encoders = await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-encoders"], timeout.Token);
            var muxers = await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-muxers"], timeout.Token);
            var candidates = SupportedEncoders(encoders, muxers);
            if (candidates.Length == 0) diagnostics.Add("Preview preflight: required H.264/AAC/HLS/FIFO capabilities are unavailable.");
            LivePreviewPreparation result = new(null, "Configured FFmpeg lacks a working H.264/AAC HLS preview path. JPEG monitoring remains available.");
            foreach (var encoder in candidates)
            {
                var probe = Path.Combine(spoolDirectory, "preview-check-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probe);
                try
                {
                    // Initialize the real codec/muxer before opening SDI. Merely listing an encoder
                    // does not establish that its operating-system runtime can initialize.
                    var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_threads", "1",
                        "-re", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=25", "-re", "-f", "lavfi", "-i",
                        "anullsrc=r=48000:cl=" + (firstPair ? "0x" + ((1 << sourceAudioChannels) - 1).ToString("x") : "stereo") };
                    AppendOutput(args, encoder, probe, "0:v:0", "1:a:0", duration: "0.24", firstPair: firstPair);
                    await MediaProcess.ReadAsync(profile.FfmpegPath, args, timeout.Token);
                    if (File.Exists(Path.Combine(probe, PlaylistName)) && Directory.EnumerateFiles(probe, "index*.ts").Any())
                    { diagnostics.Add("Preview preflight selected " + encoder.Name + " with bounded FIFO HLS/AAC output."); result = new(encoder, null); break; }
                }
                catch (IOException exception) { diagnostics.Add(DescribeFailure(encoder.Name, exception)); }
                finally { RemoveProbeFiles(probe); }
            }
            result = result with { Diagnostics = diagnostics.TakeLast(8).ToArray() };
            if (identity is not null)
            {
                if (Cache.Count >= 16) Cache.Remove(Cache.MinBy(entry => entry.Value.At).Key);
                Cache[identity] = (Stopwatch.GetTimestamp(), result);
            }
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return new(null, "Live A/V preview preflight was unavailable; JPEG monitoring remains available.")
            { Diagnostics = diagnostics.Append(DescribeFailure("runtime", exception)).TakeLast(8).ToArray() }; }
        finally { if (acquired) ProbeGate.Release(); }
    }

    private static string DescribeFailure(string encoder, Exception exception)
    {
        var message = WorkerLogTail.Sanitize($"Preview preflight {encoder}: {exception.GetType().Name}, HRESULT 0x{exception.HResult:x8}: " +
            exception.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal));
        return message.Length <= 2048 ? message : message[..2047] + "…";
    }

    internal static string CacheIdentity(string tool, int sourceAudioChannels, bool firstPair) =>
        tool + "|firstPair:" + firstPair + "|audio:" + (firstPair ? sourceAudioChannels : 2);

    internal static LivePreviewEncoder[] SupportedEncoders(string encoders, string muxers)
    {
        bool HasEncoder(string name, char type) => Regex.IsMatch(encoders, @"(?m)^\s*" + type + @"\S*\s+" + name + @"\s", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        bool HasMuxer(string name) => Regex.IsMatch(muxers, @"(?m)^\s*E\s+" + name + @"\s", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!HasEncoder("aac", 'A') || !HasMuxer("hls") || !HasMuxer("mpegts") || !HasMuxer("fifo")) return [];
        return new[] { "h264_mf", "libx264" }.Where(name => HasEncoder(name, 'V')).Select(name => new LivePreviewEncoder(name)).ToArray();
    }

    internal static void AppendOutput(List<string> args, LivePreviewEncoder encoder, string directory, string video, string audio, string? duration = null, bool firstPair = false)
    {
        args.AddRange(["-map", video, "-map", audio]);
        if (duration is not null) args.AddRange(["-t", duration]);
        var pixel = encoder.Name == "h264_mf" ? "nv12" : "yuv420p";
        args.AddRange(["-vf", "fps=25,scale=640:-2,format=" + pixel, "-c:v", encoder.Name, "-threads:v", "1", "-pix_fmt", pixel,
            "-b:v", "600k", "-g", "25", "-bf", "0", "-fps_mode:v", "passthrough"]);
        if (encoder.Name == "h264_mf") args.AddRange(["-hw_encoding", "0"]);
        else args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-sc_threshold", "0"]);
        if (firstPair) args.AddRange(["-af", "pan=stereo|c0=c0|c1=c1"]);
        args.AddRange(["-c:a", "aac", "-b:a", "96k", "-ac", "2", "-ar", "48000",
            "-f", "fifo", "-fifo_format", "hls", "-queue_size", "50", "-drop_pkts_on_overflow", "1",
            "-attempt_recovery", "1", "-recovery_wait_time", "1", "-restart_with_keyframe", "1", "-format_opts", HlsOptions, Path.Combine(directory, PlaylistName)]);
    }

    private static string? ToolIdentity(string configured)
    {
        var path = MediaToolPaths.Resolve(configured);
        if (!Path.IsPathFullyQualified(path))
        {
            var name = OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path + ".exe" : path;
            path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists) ?? path;
        }
        if (!File.Exists(path)) return null;
        var file = new FileInfo(path);
        return file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
    }
    private static void RemoveProbeFiles(string directory)
    {
        // No recursive deletion and no recording paths. Only this fresh generated probe's files.
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Retain failed cleanup evidence. */ }
    }
}
