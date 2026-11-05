using System.Globalization;
using System.Text.Json;

namespace DeckCapture.Capture;

public sealed record ValidatedMedia(double DurationSeconds, long Frames, string VideoCodec, string PixelFormat, string AudioCodec, int AudioChannels, int SampleRate);

public static class MediaValidation
{
    public static async Task<ValidatedMedia> ValidateAsync(string ffprobePath, string mediaPath, CancellationToken cancellationToken)
    {
        var json = await MediaProcess.ReadAsync(ffprobePath, BuildProbeArguments(mediaPath, Environment.ProcessorCount), cancellationToken);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
        var videoCodec = video.GetProperty("codec_name").GetString()!;
        var pixelFormat = video.GetProperty("pix_fmt").GetString()!;
        var audioCodec = audio.GetProperty("codec_name").GetString()!;
        var channels = audio.GetProperty("channels").GetInt32();
        var sampleRate = int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
        var frames = long.Parse(video.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
        var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (videoCodec != "ffv1" || pixelFormat != "yuv422p10le" || audioCodec != "pcm_s24le" || sampleRate != 48000 || frames <= 0 || duration <= 0)
            throw new InvalidDataException("Media does not match FFV1 10-bit 4:2:2 / 48 kHz PCM 24-bit capture profile, or contains no decodable frames.");
        if (!document.RootElement.GetProperty("format").GetProperty("format_name").GetString()!.Contains("matroska", StringComparison.Ordinal))
            throw new InvalidDataException("Media container is not Matroska.");
        return new(duration, frames, videoCodec, pixelFormat, audioCodec, channels, sampleRate);
    }

    // FFprobe inherits a single-thread decoder default. Bound slice parallelism
    // independently of encoding, leaving most CPU capacity available to captures.
    internal static IReadOnlyList<string> BuildProbeArguments(string mediaPath, int processorCount) =>
        ["-v", "error", "-threads:v", Math.Clamp(processorCount / 4, 1, 8).ToString(CultureInfo.InvariantCulture), "-thread_type:v", "slice",
            "-count_frames", "-show_streams", "-show_format", "-of", "json", mediaPath];
}
