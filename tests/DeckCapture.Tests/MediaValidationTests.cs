using DeckCapture.Capture;
using DeckCapture.Domain;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class MediaValidationTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(8, 2)]
    [InlineData(16, 4)]
    [InlineData(32, 8)]
    [InlineData(40, 8)]
    [InlineData(128, 8)]
    public void FullDecoderScanUsesBoundedSliceThreadsAndKeepsInputAsOneArgument(int processors, int expected)
    {
        const string path = "C:\\retained media\\master ' quoted; name.mkv.partial";
        var arguments = MediaValidation.BuildProbeArguments(path, processors).ToList();
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), arguments[arguments.IndexOf("-threads:v") + 1]);
        Assert.Equal("slice", arguments[arguments.IndexOf("-thread_type:v") + 1]);
        Assert.Equal(path, arguments[^1]); Assert.Single(arguments, argument => argument == path);
        Assert.Contains("-count_frames", arguments); Assert.Contains("-show_streams", arguments); Assert.Contains("-show_format", arguments);
        Assert.DoesNotContain("-read_intervals", arguments); Assert.DoesNotContain("-skip_frame", arguments); Assert.DoesNotContain("-count_packets", arguments);
    }

    [CaptureIntegrationFact]
    public async Task ValidationDecodesEveryFrameRatherThanTrustingDeclaredFrameMetadata()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".local", "validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "full scan ' sample.mkv");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var profile = new CaptureProfile();
        await MediaProcess.ReadAsync(profile.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-filter_threads", "1",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-map", "0:v:0", "-map", "1:a:0", "-frames:v", "70", "-t", "2.8", "-c:v", "ffv1", "-level", "3", "-threads:v", "2",
            "-pix_fmt", "yuv422p10le", "-g", "1", "-slicecrc", "1", "-c:a", "pcm_s24le", "-metadata:s:v:0", "NUMBER_OF_FRAMES=1", path], timeout.Token);
        var result = await MediaValidation.ValidateAsync(profile.FfprobePath, path, timeout.Token);
        Assert.Equal(70, result.Frames); Assert.InRange(result.DurationSeconds, 2.79, 3.1);
        Assert.Equal("ffv1", result.VideoCodec); Assert.Equal("yuv422p10le", result.PixelFormat);
        Assert.Equal("pcm_s24le", result.AudioCodec); Assert.Equal(2, result.AudioChannels); Assert.Equal(48000, result.SampleRate);
    }
}
