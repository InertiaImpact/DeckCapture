using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeckCapture.Capture;
using DeckCapture.Domain;

[assembly: InternalsVisibleTo("DeckCapture.Tests")]

namespace DeckCapture.Supervisor;

public sealed record OfflineQcOptions
{
    public int TimeoutSeconds { get; init; } = 21600;
    public int MaximumIntervals { get; init; } = 2000;
    public double InputReadRate { get; init; } = 2;
    public double BlackMinimumSeconds { get; init; } = .5;
    public double BlackPixelThreshold { get; init; } = .1;
    public double BlackPictureRatio { get; init; } = .98;
    public double SilenceDbfs { get; init; } = -50;
    public double SilenceMinimumSeconds { get; init; } = 2;
    public double FreezeNoiseDb { get; init; } = -50;
    public double FreezeMinimumSeconds { get; init; } = 2;
    public double LowLumaThreshold { get; init; } = .05;
    public double HighLumaThreshold { get; init; } = .95;
    public double HighSaturationThreshold { get; init; } = .9;
    public bool ComputeSha256 { get; init; } = true;

    internal void Validate()
    {
        var values = new[] { InputReadRate, BlackMinimumSeconds, BlackPixelThreshold, BlackPictureRatio,
            SilenceDbfs, SilenceMinimumSeconds, FreezeNoiseDb, FreezeMinimumSeconds, LowLumaThreshold, HighLumaThreshold, HighSaturationThreshold };
        if (values.Any(value => !double.IsFinite(value)) || TimeoutSeconds is < 1 or > 172800 || MaximumIntervals is < 1 or > 10000
            || InputReadRate is < .1 or > 16 || BlackMinimumSeconds is < .01 or > 3600 || BlackPixelThreshold is < 0 or > 1
            || BlackPictureRatio is < 0 or > 1 || SilenceDbfs is < -120 or > 0 || SilenceMinimumSeconds is < .01 or > 3600
            || FreezeNoiseDb is < -120 or > 0 || FreezeMinimumSeconds is < .01 or > 3600 || LowLumaThreshold is < 0 or > 1
            || HighLumaThreshold is < 0 or > 1 || LowLumaThreshold >= HighLumaThreshold || HighSaturationThreshold is < 0 or > 1)
            throw new ArgumentException("Offline QC settings exceed supported resource or threshold bounds.");
    }
}

public sealed record QcStreamInfo(int Index, string Kind, string Codec, string? PixelFormat, int? AudioChannels,
    int? SampleRate, int? BitsPerRawSample, string? FieldOrder, string? AverageFrameRate, string? ColorRange);
public sealed record QcMeasure(long Samples, double Minimum, double Maximum, double Mean);
public sealed record QcInterval(string Kind, double StartSeconds, double EndSeconds, int? AudioChannel = null,
    double? PeakValue = null, bool OpenAtEnd = false);
public sealed record OfflineQcSummary(string ReportFormat, DateTimeOffset AnalyzedAt, string SourceFileName,
    long SourceBytes, string? SourceSha256, string Container, double DurationSeconds, long DecodedFrames,
    long SignalStatsFrames, QcStreamInfo[] Streams, IReadOnlyDictionary<string, bool> AvailableFilters,
    IReadOnlyDictionary<string, QcMeasure> Measures, QcInterval[] Intervals, long OmittedIntervals,
    string[] Warnings, string[] DiagnosticTail, OfflineQcOptions Settings);
public sealed record OfflineQcProgress(string Phase, long Frames, double ProcessedSeconds, double? DurationSeconds, DateTimeOffset UpdatedAt)
{
    public double? Speed { get; init; }
}

/// <summary>Read-only, opt-in offline QC. The caller owns scheduling, report publication and review policy.</summary>
public static class OfflineQcService
{
    // A static gate also protects callers that instantiate this service per request.
    private static readonly SemaphoreSlim Concurrency = new(1, 1);
    private static readonly string[] FilterNames = ["signalstats", "metadata", "blackdetect", "silencedetect", "freezedetect"];

    public static async Task<OfflineQcSummary> AnalyzeAsync(string mediaPath, CaptureProfile profile,
        OfflineQcOptions? options, CancellationToken cancellationToken, Action<OfflineQcProgress>? progress = null)
    {
        options ??= new();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var updates = new QcProgressReporter(progress);
        updates.Report("Queued", 0, 0, null, force: true);
        await Concurrency.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var token = timeout.Token;
            updates.Report("Preflight", 0, 0, null, force: true);
            // Keeping a read-only guard open rejects concurrent writes/deletes on Windows. No output filename is sent to FFmpeg.
            var path = Path.GetFullPath(mediaPath);
            await using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sourceLength = guard.Length;
            var probeText = new StringBuilder();
            await RunAsync(profile.FfprobePath, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path],
                line => AppendBounded(probeText, line), null, token);
            using var probe = JsonDocument.Parse(probeText.ToString());
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().Select(ReadStream).ToArray();
            var video = streams.FirstOrDefault(stream => stream.Kind == "video") ?? throw new InvalidDataException("QC requires a video stream.");
            var format = probe.RootElement.GetProperty("format");
            var duration = ParseNumber(GetString(format, "duration")) ?? throw new InvalidDataException("Media has no finite duration.");
            if (duration <= 0 || duration > 172800) throw new InvalidDataException("Offline QC media duration must be positive and at most 48 hours.");
            var filtersText = new StringBuilder();
            await RunAsync(profile.FfmpegPath, ["-hide_banner", "-filters"], line => AppendBounded(filtersText, line), null, token);
            var available = FilterNames.ToDictionary(name => name, name => HasFilter(filtersText.ToString(), name));
            var warnings = new List<string>();
            foreach (var filter in available.Where(pair => !pair.Value)) warnings.Add($"Filter {filter.Key} unavailable; corresponding measurements were not performed.");
            if (streams.Count(stream => stream.Kind == "video") > 1 || streams.Count(stream => stream.Kind == "audio") > 1)
                warnings.Add("Analysis covers the first video and first audio stream only; additional streams were probed but not decoded.");
            var audio = streams.FirstOrDefault(stream => stream.Kind == "audio");
            if (audio is null) warnings.Add("No audio stream: silence analysis was not performed.");
            var depth = video.BitsPerRawSample is >= 8 and <= 16 ? video.BitsPerRawSample.Value : PixelDepth(video.PixelFormat);
            if (video.PixelFormat is null || !video.PixelFormat.StartsWith("yuv", StringComparison.Ordinal))
                warnings.Add("Signalstats may convert this source pixel format. Luma/saturation normalization assumes integer YUV code values; inspect interpretation before using thresholds.");
            var parser = new QcLogParser(options, depth, FrameDuration(video.AverageFrameRate));
            var videoFilters = new List<string>();
            if (available["signalstats"] && available["metadata"]) videoFilters.Add("signalstats=stat=tout+vrep+brng,metadata=mode=print");
            if (available["blackdetect"]) videoFilters.Add($"blackdetect=d={Number(options.BlackMinimumSeconds)}:pix_th={Number(options.BlackPixelThreshold)}:pic_th={Number(options.BlackPictureRatio)}");
            if (available["freezedetect"]) videoFilters.Add($"freezedetect=noise={Number(options.FreezeNoiseDb)}dB:duration={Number(options.FreezeMinimumSeconds)}");
            var arguments = BuildAnalysisArguments(path, options, videoFilters, audio is not null && available["silencedetect"], Environment.ProcessorCount);
            long decodedFrames = 0;
            double processedSeconds = 0;
            double? speed = null;
            var tail = new Queue<string>();
            updates.Report("Analyzing", 0, 0, duration, force: true);
            await RunAsync(profile.FfmpegPath, arguments, line =>
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frames)) decodedFrames = frames;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12).Trim(), out var microseconds))
                    processedSeconds = Math.Max(processedSeconds, Math.Clamp(microseconds / 1_000_000d, 0, duration));
                if (line.StartsWith("speed=", StringComparison.Ordinal)) speed = ParseNumber(line[6..].Trim().TrimEnd('x')) is { } value && value >= 0 ? value : null;
                if (line.StartsWith("progress=", StringComparison.Ordinal))
                    updates.Report("Analyzing", decodedFrames, processedSeconds, duration, speed, force: line == "progress=end");
            }, line =>
            {
                parser.Accept(line);
                if (!line.Contains("lavfi.signalstats.", StringComparison.Ordinal) && !line.Contains("[Parsed_metadata_", StringComparison.Ordinal))
                { tail.Enqueue(line.Length <= 2048 ? line : line[..2048]); while (tail.Count > 64) tail.Dequeue(); }
            }, token);
            if (decodedFrames <= 0) throw new InvalidDataException("Full QC decode produced no video frames.");
            parser.Complete(duration);
            if (available["signalstats"] && available["metadata"] && parser.FrameCount != decodedFrames)
                warnings.Add($"Signalstats measured {parser.FrameCount} frames while FFmpeg reported {decodedFrames} decoded/output frames; completeness needs review.");
            if (parser.OmittedIntervals > 0) warnings.Add($"Interval report cap reached; {parser.OmittedIntervals} additional detected intervals were omitted.");
            if (parser.NonMonotonicTimestamps) warnings.Add("Non-monotonic analysis timestamps were observed; interval timing needs review.");
            if (options.ComputeSha256) updates.Report("Hashing", decodedFrames, processedSeconds, duration, force: true);
            var hash = options.ComputeSha256 ? Convert.ToHexStringLower(await SHA256.HashDataAsync(guard, token)) : null;
            warnings.Add("Findings are threshold measurements, not pass/fail preservation certification. Static images, leader and intentionally silent material can be legitimate.");
            updates.Report("Complete", decodedFrames, duration, duration, force: true);
            return new("deckcapture.offline-qc.v1", DateTimeOffset.UtcNow, Path.GetFileName(path), sourceLength, hash,
                GetString(format, "format_name") ?? "unknown", duration, decodedFrames, parser.FrameCount, streams, available,
                parser.Measures, parser.Intervals.ToArray(), parser.OmittedIntervals, warnings.ToArray(), tail.ToArray(), options);
        }
        finally { Concurrency.Release(); }
    }

    internal static IReadOnlyList<string> BuildAnalysisArguments(string path, OfflineQcOptions options, IEnumerable<string> videoFilters, bool analyzeSilence, int processorCount)
    {
        // The FFV1 decoder and slice-capable signalstats/blackdetect filters have
        // separate bounded budgets; serial temporal/audio filters stay serial.
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "info", "-nostats", "-stats_period", "1", "-xerror", "-err_detect", "explode",
            "-threads:v", Math.Clamp(processorCount / 4, 1, 8).ToString(CultureInfo.InvariantCulture), "-thread_type:v", "slice", "-threads:a", "1",
            "-filter_threads", Math.Clamp(processorCount / 8, 1, 4).ToString(CultureInfo.InvariantCulture), "-filter_complex_threads", "1",
            "-readrate", Number(options.InputReadRate), "-i", path, "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" };
        var filters = string.Join(',', videoFilters);
        if (filters.Length > 0) arguments.AddRange(["-vf", filters]);
        if (analyzeSilence) arguments.AddRange(["-af", $"silencedetect=noise={Number(options.SilenceDbfs)}dB:duration={Number(options.SilenceMinimumSeconds)}:mono=1"]);
        arguments.AddRange(["-fps_mode", "passthrough", "-threads", "1", "-progress", "pipe:1", "-f", "null", "-"]);
        return arguments;
    }

    private static QcStreamInfo ReadStream(JsonElement stream) => new(stream.GetProperty("index").GetInt32(),
        GetString(stream, "codec_type") ?? "unknown", GetString(stream, "codec_name") ?? "unknown", GetString(stream, "pix_fmt"),
        GetInt(stream, "channels"), GetInt(stream, "sample_rate"), GetInt(stream, "bits_per_raw_sample"), GetString(stream, "field_order"),
        GetString(stream, "avg_frame_rate"), GetString(stream, "color_range"));
    private static string? GetString(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? field.ToString() : null;
    private static int? GetInt(JsonElement value, string name) => int.TryParse(GetString(value, name), out var number) ? number : null;
    private static double? ParseNumber(string? text) => double.TryParse(text, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static int PixelDepth(string? format)
    {
        var match = Regex.Match(format ?? "", @"p(9|10|12|14|16)(?:le|be)$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 8;
    }
    private static double FrameDuration(string? rate)
    {
        var parts = rate?.Split('/');
        if (parts?.Length == 2 && ParseNumber(parts[0]) is { } numerator && ParseNumber(parts[1]) is { } denominator && numerator > 0 && denominator > 0)
            return Math.Clamp(denominator / numerator, .001, 10);
        return .04;
    }
    private static void AppendBounded(StringBuilder builder, string line)
    {
        if (builder.Length + line.Length > 1024 * 1024) throw new InvalidDataException("QC tool metadata exceeded the 1 MiB limit.");
        builder.AppendLine(line);
    }

    private static async Task RunAsync(string executable, IEnumerable<string> arguments, Action<string>? stdout, Action<string>? stderr, CancellationToken token)
    {
        var info = new ProcessStartInfo(MediaToolPaths.Resolve(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start configured QC tool.");
        // Concurrency, read rate and decoder/filter budgets bound QC's resource use.
        // Keep normal scheduling: priority demotion can indefinitely starve even
        // short probes and tiny decodes while normal-priority captures are running.
        using var registration = token.Register(() => Kill(process));
        var errors = new Queue<string>();
        async Task DrainAsync(StreamReader reader, Action<string>? receive, bool error)
        {
            try
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (error) { errors.Enqueue(line.Length <= 2048 ? line : line[..2048]); while (errors.Count > 12) errors.Dequeue(); }
                    receive?.Invoke(line);
                }
            }
            catch { Kill(process); throw; }
        }
        var outputTask = DrainAsync(process.StandardOutput, stdout, false);
        var errorTask = DrainAsync(process.StandardError, stderr, true);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(outputTask, errorTask);
            if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} QC exited {process.ExitCode}: {string.Join(Environment.NewLine, errors)}");
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(outputTask, errorTask); } catch (Exception) { /* Preserve the original process/cancellation failure. */ }
            throw;
        }
    }
    internal static bool HasFilter(string listing, string name) =>
        // FFmpeg 9 removed the command-support flag column. Accept its two-column
        // table and earlier three-column tables, requiring a real media-pad signature.
        Regex.IsMatch(listing, @"(?m)^[ \t]*[TSC.]{2,3}[ \t]+" + Regex.Escape(name) + @"[ \t]+[AVN|]+->[AVN|]+(?:[ \t\r]|$)");

    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
}

// Progress is telemetry, never an owner of the media operation. A UI/persistence
// callback failure must not terminate a successful read-only QC pass.
internal sealed class QcProgressReporter(Action<OfflineQcProgress>? receive, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private long? lastUpdate;
    private string? lastPhase;

    public void Report(string phase, long frames, double seconds, double? duration, double? speed = null, bool force = false)
    {
        if (receive is null) return;
        var now = time.GetTimestamp();
        if (!force && phase == lastPhase && lastUpdate is { } previous && time.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(1)) return;
        lastUpdate = now; lastPhase = phase;
        try { receive(new(phase, frames, seconds, duration, time.GetUtcNow()) { Speed = speed }); }
        catch (Exception) { /* Optional progress consumers cannot change the QC result. */ }
    }
}

// Parser retains aggregates and bounded intervals, never one record per decoded frame.
internal sealed class QcLogParser(OfflineQcOptions options, int bitDepth, double frameDuration)
{
    private static readonly Regex Frame = new(@"\bframe:\s*\d+\s+pts:\s*\S+\s+pts_time:(?<time>-?[0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex Stat = new(@"lavfi\.signalstats\.(?<key>[A-Z]+)=(?<value>[-+0-9.eE]+)", RegexOptions.Compiled);
    private static readonly Regex Event = new(@"(?<kind>black_start|black_end|silence_start|silence_end|freeze_start|freeze_end):\s*(?<value>[-+0-9.eE]+)", RegexOptions.Compiled);
    private static readonly Regex AudioChannel = new(@"\bchannel:\s*(?<channel>\d+)", RegexOptions.Compiled);
    private static readonly HashSet<string> Keys = new("YMIN YLOW YAVG YHIGH YMAX UMIN ULOW UAVG UHIGH UMAX VMIN VLOW VAVG VHIGH VMAX SATMIN SATLOW SATAVG SATHIGH SATMAX HUEMED HUEAVG YDIF UDIF VDIF YBITDEPTH UBITDEPTH VBITDEPTH TOUT VREP BRNG".Split(' '), StringComparer.Ordinal);
    private readonly Dictionary<string, Accumulator> aggregates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> frameValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QcInterval> active = new(StringComparer.Ordinal);
    private double? currentFrameTime;
    private double lastFrameEnd;
    public long FrameCount { get; private set; }
    public long OmittedIntervals { get; private set; }
    public bool NonMonotonicTimestamps { get; private set; }
    public List<QcInterval> Intervals { get; } = [];
    public IReadOnlyDictionary<string, QcMeasure> Measures => aggregates.ToDictionary(pair => pair.Key, pair => pair.Value.Value);

    public void Accept(string line)
    {
        var frame = Frame.Match(line);
        if (frame.Success && Number(frame.Groups["time"].Value) is { } time)
        {
            if (currentFrameTime is { } previous && time < previous) NonMonotonicTimestamps = true;
            FlushFrame(time);
            currentFrameTime = time;
            frameValues.Clear();
        }
        var stat = Stat.Match(line);
        if (stat.Success && Keys.Contains(stat.Groups["key"].Value) && Number(stat.Groups["value"].Value) is { } value)
            frameValues[stat.Groups["key"].Value] = value;
        var channelMatch = AudioChannel.Match(line);
        var channel = channelMatch.Success && int.TryParse(channelMatch.Groups["channel"].Value, out var parsed) && parsed < 32 ? parsed : (int?)null;
        foreach (Match item in Event.Matches(line))
        {
            if (Number(item.Groups["value"].Value) is not { } timestamp) continue;
            var name = item.Groups["kind"].Value;
            var kind = name[..name.LastIndexOf('_')];
            var eventChannel = kind == "silence" ? channel : null;
            var key = kind + ":" + eventChannel;
            if (name.EndsWith("_start", StringComparison.Ordinal)) active.TryAdd(key, new(kind, Math.Max(0, timestamp), Math.Max(0, timestamp), eventChannel));
            else if (active.Remove(key, out var interval)) Add(interval with { EndSeconds = Math.Max(interval.StartSeconds, timestamp) });
        }
    }

    private void FlushFrame(double nextTime)
    {
        if (currentFrameTime is not { } start || frameValues.Count == 0) return;
        FrameCount++;
        var end = Math.Max(start, nextTime);
        lastFrameEnd = Math.Max(lastFrameEnd, end);
        foreach (var pair in frameValues)
            Accumulate(pair.Key, pair.Value);
        var scale = Math.Pow(2, bitDepth) - 1;
        var saturationScale = Math.Sqrt(2) * Math.Pow(2, bitDepth - 1);
        foreach (var (source, target, divisor) in new[] {
            ("YMIN", "lumaMinimumNormalized", scale), ("YMAX", "lumaMaximumNormalized", scale), ("YAVG", "lumaAverageNormalized", scale),
            ("SATMAX", "saturationMaximumNormalized", saturationScale), ("SATAVG", "saturationAverageNormalized", saturationScale) })
            if (frameValues.TryGetValue(source, out var measured)) Accumulate(target, measured / divisor);
        Threshold("lowLuma", "YMIN", options.LowLumaThreshold, scale, start, end, true);
        Threshold("highLuma", "YMAX", options.HighLumaThreshold, scale, start, end, false);
        Threshold("highSaturation", "SATMAX", options.HighSaturationThreshold, saturationScale, start, end, false);
    }

    private void Accumulate(string key, double value)
    {
        if (!aggregates.TryGetValue(key, out var accumulator)) { accumulator = new(); aggregates[key] = accumulator; }
        accumulator.Add(value);
    }

    private void Threshold(string kind, string metric, double threshold, double scale, double start, double end, bool low)
    {
        if (!frameValues.TryGetValue(metric, out var raw)) return;
        var value = raw / scale;
        var flagged = low ? value < threshold : value > threshold;
        if (flagged)
        {
            if (active.TryGetValue(kind, out var interval))
                active[kind] = interval with { EndSeconds = end, PeakValue = low ? Math.Min(interval.PeakValue ?? value, value) : Math.Max(interval.PeakValue ?? value, value) };
            else active[kind] = new(kind, Math.Max(0, start), Math.Max(0, end), PeakValue: value);
        }
        else if (active.Remove(kind, out var completed)) Add(completed);
    }

    public void Complete(double duration)
    {
        FlushFrame((currentFrameTime ?? 0) + frameDuration);
        foreach (var interval in active.Values)
        {
            var end = interval.Kind == "silence" ? duration : Math.Max(interval.EndSeconds, lastFrameEnd > 0 ? lastFrameEnd : duration);
            Add(interval with { EndSeconds = Math.Max(interval.StartSeconds, end), OpenAtEnd = true });
        }
        active.Clear();
        currentFrameTime = null;
        Intervals.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
    }

    private void Add(QcInterval interval) { if (Intervals.Count < options.MaximumIntervals) Intervals.Add(interval); else OmittedIntervals++; }
    private static double? Number(string text) => double.TryParse(text, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    private sealed class Accumulator
    {
        private long count;
        private double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity, mean;
        public void Add(double value) { count++; minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); mean += (value - mean) / count; }
        public QcMeasure Value => new(count, minimum, maximum, mean);
    }
}
