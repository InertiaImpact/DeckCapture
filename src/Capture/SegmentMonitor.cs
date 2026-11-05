using System.Globalization;
using System.Text.Json;
using DeckCapture.Domain;

namespace DeckCapture.Capture;

internal sealed class SegmentMonitor
{
    // One analyzer belongs to one owned worker; no process-wide/path-keyed cache grows with segments.
    private int? sourceAudioChannels;
    private string? detectedVideoFormat;
    public async Task<CaptureMetrics> AnalyzeAsync(CaptureProfile profile, string segment, string directory, bool synthetic)
    {
        var gray = Path.Combine(directory, "monitor.gray");
        var audio = Path.Combine(directory, "monitor.pcm");
        var channelAudio = Path.Combine(directory, "monitor-channels.pcm");
        var preview = Path.Combine(directory, "preview.jpg");
        var temporaryPreview = Path.Combine(directory, "preview.next.jpg");
        if (sourceAudioChannels is null || detectedVideoFormat is null)
        {
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var json = await MediaProcess.ReadAsync(profile.FfprobePath, ["-v", "error", "-read_intervals", "%+#1",
                    "-show_entries", "stream=codec_type,channels,width,height,field_order,avg_frame_rate,r_frame_rate", "-of", "json", segment], probeTimeout.Token);
                var observed = ParseStreamMetadata(json);
                sourceAudioChannels ??= observed.AudioChannels;
                detectedVideoFormat ??= observed.VideoFormat;
            }
            catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested) { /* An optional probe cannot consume the primary analysis deadline. */ }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
            { /* Channel meters remain unavailable; existing mono analysis can continue. */ }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-filter_threads", "1", "-threads", "1", "-i", segment,
            "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=64:36,format=gray", "-f", "rawvideo", gray,
            "-map", "0:a:0", "-t", "0.25", "-ac", "1", "-ar", "48000", "-f", "s16le", audio,
            "-map", "0:a:0", "-t", "0.25", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", channelAudio,
            "-map", "0:v:0", "-frames:v", "1", "-vf", "scale=320:-2", "-update", "1", temporaryPreview], timeout.Token);
        var pixels = await File.ReadAllBytesAsync(gray, timeout.Token);
        var pcm = await File.ReadAllBytesAsync(audio, timeout.Token);
        if (pixels.Length != 64 * 36 || pcm.Length < 2) throw new InvalidDataException("Incomplete monitor samples.");
        File.Move(temporaryPreview, preview, overwrite: true);
        var analysis = AnalyzeSamples(pixels, pcm);
        // Cache actual stream channel count from a bounded header probe. Never force stereo:
        // doing so would invent a second channel for mono and mix multichannel inputs.
        var channelLevels = await ReadChannelSampleAsync(channelAudio, sourceAudioChannels ?? 0, timeout.Token);
        return new CaptureMetrics
        {
            SignalLocked = synthetic ? true : null, InputSignalPresent = synthetic ? true : null,
            InputSignalStatusSource = synthetic ? "Synthetic" : "Unavailable", AnalysisAvailable = true,
            BlackRatio = analysis.BlackRatio, IsBlack = analysis.BlackRatio >= .98,
            AudioRmsDbfs = analysis.AudioRmsDbfs, AudioChannelRmsDbfs = channelLevels, LumaWaveform = analysis.Waveform, PreviewPath = preview,
            DetectedVideoFormat = detectedVideoFormat,
            Warning = synthetic ? "Synthetic source. Metrics sample one frame / 250 ms audio per completed segment (about 1–2 s latency)."
                : "Hardware lock is unknown in this FFmpeg adapter. Input presence uses reported DeckLink frame-flag transitions when available. Drop count covers FFmpeg pipeline reports only, not device loss; analysis has about 1–2 s latency."
        };
    }

    internal static (int? AudioChannels, string? VideoFormat) ParseStreamMetadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return (null, null);
        int? channels = null; string? format = null;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object) continue;
            var kind = MetadataString(stream, "codec_type");
            if (kind == "audio" && channels is null && MetadataInt(stream, "channels") is >= 1 and <= 16) channels = MetadataInt(stream, "channels");
            if (kind != "video" || format is not null || MetadataInt(stream, "width") is not { } width || MetadataInt(stream, "height") is not { } height ||
                width is < 1 or > 32768 || height is < 1 or > 32768) continue;
            var fields = MetadataString(stream, "field_order") switch
            {
                "progressive" => "progressive", "tt" or "bb" or "tb" or "bt" => "interlaced", _ => "field order unknown"
            };
            var rate = ParseFrameRate(MetadataString(stream, "avg_frame_rate")) ?? ParseFrameRate(MetadataString(stream, "r_frame_rate"));
            format = $"{width}×{height} {fields}" + (rate is { } fps ? " @ " + fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps" : " (rate unknown)");
        }
        return (channels, format);
    }

    private static string? MetadataString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? MetadataInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static double? ParseFrameRate(string? value)
    {
        var parts = value?.Split('/');
        if (parts?.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) || denominator <= 0) return null;
        var rate = numerator / denominator;
        return double.IsFinite(rate) && rate is > 0 and <= 1000 ? rate : null;
    }

    internal static async Task<double[]> ReadChannelSampleAsync(string path, int channels, CancellationToken token)
    {
        if (channels is < 1 or > 16) return [];
        try
        {
            if (new FileInfo(path).Length > 512 * 1024) return [];
            return AnalyzeChannelPcm(await File.ReadAllBytesAsync(path, token), channels);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return []; }
    }

    internal static double[] AnalyzeChannelPcm(ReadOnlySpan<byte> pcm, int channels)
    {
        if (channels is < 1 or > 16 || pcm.IsEmpty || pcm.Length % (channels * 2) != 0) return [];
        var frames = pcm.Length / (channels * 2);
        var levels = new double[Math.Min(channels, 2)];
        for (var channel = 0; channel < levels.Length; channel++)
        {
            double squares = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice((frame * channels + channel) * 2, 2)) / 32768d;
                squares += sample * sample;
            }
            var rms = Math.Sqrt(squares / frames);
            levels[channel] = rms <= 0 ? -100 : Math.Max(-100, 20 * Math.Log10(rms));
        }
        return levels;
    }

    // Reduced full-range luma and mono PCM are monitoring proxies, not preservation QC.
    internal static (double BlackRatio, double AudioRmsDbfs, double[] Waveform) AnalyzeSamples(byte[] pixels, byte[] pcm)
    {
        if (pixels.Length != 64 * 36 || pcm.Length < 2 || pcm.Length % 2 != 0) throw new ArgumentException("Invalid monitoring sample sizes.");
        var black = pixels.Count(pixel => pixel <= 16) / (double)pixels.Length;
        var waveform = new double[64];
        for (var column = 0; column < 64; column++)
        {
            double total = 0;
            for (var row = 0; row < 36; row++) total += pixels[row * 64 + column];
            waveform[column] = total / 36 / 255;
        }
        double squares = 0;
        for (var index = 0; index < pcm.Length; index += 2)
        {
            var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index, 2)) / 32768d;
            squares += sample * sample;
        }
        var rms = Math.Sqrt(squares / (pcm.Length / 2));
        return (black, rms <= 0 ? -100 : Math.Max(-100, 20 * Math.Log10(rms)), waveform);
    }
}
