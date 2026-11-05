using DeckCapture.Capture;
using Xunit;

namespace DeckCapture.Tests;

public sealed class MediaToolPathsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DeckCapture-ToolResolution-" + Guid.NewGuid().ToString("N"));

    private string Bundle()
    {
        var bundle = Path.Combine(root, "media-tools");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, "ffmpeg.exe"), []);
        File.WriteAllBytes(Path.Combine(bundle, "ffprobe.exe"), []);
        return bundle;
    }

    [Fact]
    public void SupervisorAndWorkerUseTheSamePairedBundle()
    {
        var bundle = Bundle();
        Assert.Equal(Path.Combine(bundle, "ffmpeg.exe"), MediaToolPaths.Resolve("ffmpeg", root, true));
        Assert.Equal(Path.Combine(bundle, "ffprobe.exe"), MediaToolPaths.Resolve("ffprobe.exe", Path.Combine(root, "capture-worker"), true));
    }

    [Fact]
    public void ExplicitPathsAndUnrelatedExecutablesKeepTheirOriginalMeaning()
    {
        Bundle();
        foreach (var path in new[] { @"C:\custom\ffmpeg.exe", "./ffmpeg", "other-tool", @"custom\ffprobe.exe" })
            Assert.Equal(path, MediaToolPaths.Resolve(path, root, true));
    }

    [Fact]
    public void MissingBundleUsesPathAndDoesNotSearchArbitraryAncestors()
    {
        Bundle();
        Assert.Equal("ffmpeg", MediaToolPaths.Resolve("ffmpeg", Path.Combine(root, "unrelated"), true));
        Assert.Equal("ffprobe", MediaToolPaths.Resolve("ffprobe", Path.Combine(root, "missing", "capture-worker"), true));
    }

    [Fact]
    public void IncompleteBundleFailsRatherThanMixingInstalledVersions()
    {
        Directory.CreateDirectory(Path.Combine(root, "media-tools"));
        File.WriteAllBytes(Path.Combine(root, "media-tools", "ffmpeg.exe"), []);
        Assert.Throws<InvalidDataException>(() => MediaToolPaths.Resolve("ffmpeg", root, true));
        Assert.Throws<InvalidDataException>(() => MediaToolPaths.Resolve("ffprobe", root, true));
    }

    public void Dispose()
    {
        // Delete only this test's checked, unique directory; it contains no media.
        if (Directory.Exists(root) && Path.GetFileName(root).StartsWith("DeckCapture-ToolResolution-", StringComparison.Ordinal))
            Directory.Delete(root, true);
    }
}
