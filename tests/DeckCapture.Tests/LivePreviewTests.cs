using System.Text;
using DeckCapture.Capture;
using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

[Collection(MediaIntegrationCollection.Name)]
public sealed class LivePreviewTests
{
    [Theory]
    [InlineData("h264_mf", "nv12")]
    [InlineData("libx264", "yuv420p")]
    public void HardwarePreviewReusesOneInputAndKeepsAllPreservationOptionsBeforeItsSeparateOutput(string encoder, string pixel)
    {
        var options = new CaptureOpenOptions(new() { SourceKind = "DeckLink", DeviceName = "Exact SDI", FormatCode = "auto", AudioChannels = 16 },
            new() { EncoderThreads = 8 }, "unused");
        var args = SpoolCapture.BuildEncoderArguments(options, "unused", new(encoder)).ToList();
        Assert.Single(args, arg => arg == "-i");
        var master = args.FindIndex(arg => arg.EndsWith("segment-%09d.mkv", StringComparison.Ordinal));
        Assert.True(master > args.IndexOf("-c:v"));
        Assert.Equal("ffv1", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("pcm_s24le", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("8", args[args.IndexOf("-threads:v") + 1]);
        Assert.Equal("passthrough", args[args.IndexOf("-fps_mode:v") + 1]);
        Assert.True(args.IndexOf("-vf") > master);
        Assert.Equal("fps=25,scale=640:-2,format=" + pixel, args[args.IndexOf("-vf") + 1]);
        Assert.True(args.IndexOf("pan=stereo|c0=c0|c1=c1") > master);
        Assert.True(args.IndexOf("aac") > master);
        Assert.Contains("hls_list_size=6", args[args.IndexOf("-format_opts") + 1]);
        Assert.Contains("hls_delete_threshold=2", args[args.IndexOf("-format_opts") + 1]);
        Assert.Equal("50", args[args.IndexOf("-queue_size") + 1]);
        Assert.Equal(Path.Combine("unused", "preview-hls", "index.m3u8"), args[^1]);
        if (encoder == "h264_mf") { Assert.Contains("-hw_encoding", args); Assert.DoesNotContain("-preset", args); }
    }

    [Fact]
    public void DisableLeavesThePreservationCommandUnchangedAndCacheSeparatesPanQualification()
    {
        var options = new CaptureOpenOptions(new(), new() { LivePreviewEnabled = false }, "unused");
        Assert.Equal(SpoolCapture.BuildEncoderArguments(options, "unused"), SpoolCapture.BuildEncoderArguments(options, "unused", new("h264_mf")));
        Assert.NotEqual(LivePreview.CacheIdentity("tool", 2, false), LivePreview.CacheIdentity("tool", 2, true));
        Assert.NotEqual(LivePreview.CacheIdentity("tool", 8, true), LivePreview.CacheIdentity("tool", 16, true));
    }

    [Fact]
    public void CodecSelectionRequiresAllMediaFeaturesAndPrefersSoftwareMf()
    {
        const string codecs = " V....D h264_mf Media Foundation\n V....D libx264 H264\n A..... aac AAC\n";
        const string muxers = " E hls Apple\n E mpegts MPEGTS\n E fifo FIFO\n";
        Assert.Equal(new[] { "h264_mf", "libx264" }, LivePreview.SupportedEncoders(codecs, muxers).Select(codec => codec.Name));
        Assert.Empty(LivePreview.SupportedEncoders(" V.S..D ffv1 FFV1\n", muxers));
        Assert.Empty(LivePreview.SupportedEncoders(codecs, " E hls Apple\n E mpegts MPEGTS\n"));
        Assert.Empty(LivePreview.SupportedEncoders(codecs.Replace(" A..... aac AAC\n", "", StringComparison.Ordinal), muxers));
    }

    [Fact]
    public void PreviewDuplicationCannotBecomeAPreservationIntegrityWarning()
    {
        var integrity = new EncoderIntegrityMonitor();
        integrity.Observe("[vf#1:0 @ 00000001] More than 1000 frames duplicated");
        integrity.Observe("[vost#1:0/h264_mf @ 00000001] More than 1000 frames duplicated");
        Assert.False(integrity.OutputFrameDuplication);
        integrity.Observe("[vf#0:0 @ 00000001] More than 1000 frames duplicated");
        Assert.True(integrity.OutputFrameDuplication);
    }

    [Theory]
    [InlineData("../master.mkv")]
    [InlineData("index0.ts.tmp")]
    [InlineData("script.js")]
    [InlineData("index0.ts?token=secret")]
    [InlineData("index0000000000.ts")]
    [InlineData("https://other.example/segment.ts")]
    public void PlaylistRejectsAnythingExceptBoundedGeneratedMediaNames(string uri) =>
        Assert.False(LivePreviewFiles.ValidManifest("#EXTM3U\n#EXTINF:1,\n" + uri + "\n"));

    [Fact]
    public async Task FileServingIsConfinedToTheExactJobGeneratedPreviewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "deckcapture-hls-" + Guid.NewGuid().ToString("N"));
        var job = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, ".spool", job, "spool-" + Guid.NewGuid().ToString("N"), "preview-hls");
        Directory.CreateDirectory(directory);
        try
        {
            var playlist = Path.Combine(directory, "index.m3u8");
            await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nindex0.ts\n", new UTF8Encoding(false));
            await File.WriteAllBytesAsync(Path.Combine(directory, "index0.ts"), [0x47, 0, 0]);
            var manifest = await LivePreviewFiles.ReadAsync(root, job, playlist, "index.m3u8", CancellationToken.None);
            Assert.Equal("application/vnd.apple.mpegurl", manifest!.ContentType);
            Assert.Equal("video/mp2t", (await LivePreviewFiles.ReadAsync(root, job, playlist, "index0.ts", CancellationToken.None))!.ContentType);
            Assert.Null(LivePreviewFiles.Resolve(root, Guid.NewGuid().ToString("N"), playlist, "index0.ts"));
            Assert.Null(LivePreviewFiles.Resolve(root, job, playlist, "../index0.ts"));
            Assert.Null(LivePreviewFiles.Resolve(root, job, Path.Combine(root, "index.m3u8"), "index0.ts"));
            var jpeg = Path.Combine(Directory.GetParent(directory)!.FullName, "preview.jpg");
            Assert.Equal(jpeg, LivePreviewFiles.ResolveJpeg(root, job, jpeg));
            Assert.Null(LivePreviewFiles.ResolveJpeg(root, Guid.NewGuid().ToString("N"), jpeg));
            Assert.Null(LivePreviewFiles.ResolveJpeg(root, job, Path.Combine(root, "preview.jpg")));
            Assert.Null(await LivePreviewFiles.ReadAsync(root, job, playlist, "index1.ts", CancellationToken.None));
            await File.WriteAllTextAsync(playlist, "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"elsewhere\"\nindex0.ts\n");
            Assert.Null(await LivePreviewFiles.ReadAsync(root, job, playlist, "index.m3u8", CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [CaptureIntegrationFact]
    public async Task SyntheticLiveAvUsesTheOwnedEncoderAndKeepsItsRollingCacheBoundedWhileMasterValidates()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".local", "av-preview-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var profile = new CaptureProfile { SyntheticWidth = 320, SyntheticHeight = 240, PreRollSeconds = 1, LivePreviewEnabled = true };
        await using var session = await new CaptureBackend().OpenAsync(new(new(), profile, root), deadline.Token);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!session.Metrics.LivePreviewAvailable && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100, deadline.Token);
        Assert.True(session.Metrics.LivePreviewAvailable, session.Metrics.LivePreviewError);
        Assert.Contains("Preview preflight selected", await File.ReadAllTextAsync(Path.Combine(root, "encoder.log"), deadline.Token));
        var playlist = session.Metrics.LivePreviewPath!;
        await Task.Delay(8500, deadline.Token); // Long enough to evict initial one-second segments.
        Assert.True(LivePreviewFiles.ValidManifest(await File.ReadAllTextAsync(playlist, deadline.Token)));
        Assert.InRange(Directory.GetFiles(Path.GetDirectoryName(playlist)!, "index*.ts").Length, 2, 10);
        var streams = await MediaProcess.ReadAsync(profile.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_name,codec_type,width,height,sample_rate,channels", "-of", "json", playlist], deadline.Token);
        Assert.Contains("h264", streams); Assert.Contains("aac", streams); Assert.Contains("48000", streams);
        var master = Path.Combine(root, "master.mkv");
        await session.BeginRecordingAsync(master, 3, 1, deadline.Token);
        var result = await session.Completion.WaitAsync(deadline.Token);
        Assert.True(result.Success, result.Error);
        var validation = await MediaValidation.ValidateAsync(profile.FfprobePath, master, deadline.Token);
        Assert.Equal(2, validation.AudioChannels); Assert.True(validation.Frames > 0);
        Assert.Equal(0, result.DroppedFrames);
        Assert.False(session.Metrics.OutputFrameDuplication);
    }
}
