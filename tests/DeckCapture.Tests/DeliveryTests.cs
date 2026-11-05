using System.Diagnostics;
using System.Security.Cryptography;
using DeckCapture.Delivery;
using DeckCapture.Domain;

namespace DeckCapture.Tests;

public sealed class DeliveryTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "deckcapture-delivery-tests-" + Guid.NewGuid().ToString("N"));
    private static DeliveryRates Fast() => new(100, 100);
    private sealed class CallbackProgress(Action<DeliveryProgress> callback) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => callback(value);
    }

    private DeliveryTransferRequest Package(string id = "package-1", int size = 256 * 1024)
    {
        var source = Path.Combine(temp, "source", id);
        Directory.CreateDirectory(Path.Combine(source, "metadata"));
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        File.WriteAllBytes(Path.Combine(source, "master.mkv"), bytes);
        File.WriteAllText(Path.Combine(source, "metadata", "qc.json"), "{\"valid\":true}");
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(path =>
            new ArtifactFile(Path.GetRelativePath(source, path), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
        return new(id, source, Path.Combine(temp, "destination"), "collection", Path.Combine(temp, "checkpoint"), files);
    }

    private static string Partial(DeliveryTransferRequest request) => Directory.GetDirectories(Path.Combine(request.DestinationRoot, ".deckcapture-staging")).Single();

    [Fact]
    public async Task PublishesVerifiedPackageAndReconcilesSameIdentity()
    {
        var request = Package();
        var engine = new FileDeliveryEngine();
        var result = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(Path.Combine(request.DestinationRoot, "collection", request.JobId), result.DestinationPath);
        Assert.True(File.Exists(Path.Combine(result.DestinationPath, "COMPLETE.json")));
        foreach (var file in request.Files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(request.SourceDirectory, file.RelativePath)), File.ReadAllBytes(Path.Combine(result.DestinationPath, file.RelativePath)));
        var again = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        Assert.Equal(result, again);
        Assert.Empty(Directory.GetDirectories(Path.Combine(request.DestinationRoot, ".deckcapture-staging")));
    }

    [Fact]
    public async Task CancellationRetainsResumablePartialWithoutCompletionMarker()
    {
        var request = Package();
        var engine = new FileDeliveryEngine();
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Phase == "Copying") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TransferAsync(request, Fast, progress, cancellation.Token));
        var staged = Partial(request);
        Assert.False(File.Exists(Path.Combine(staged, "COMPLETE.json")));
        Assert.True(new FileInfo(Path.Combine(staged, "master.mkv")).Length < request.Files.Single(f => f.RelativePath == "master.mkv").Size);
        var result = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task SameLengthCorruptPrefixIsPreservedAndRejected()
    {
        var request = Package();
        var engine = new FileDeliveryEngine();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TransferAsync(request, Fast,
            new CallbackProgress(value => { if (value.Phase == "Copying") cancellation.Cancel(); }), cancellation.Token));
        var staged = Partial(request);
        var path = Path.Combine(staged, "master.mkv");
        var corrupt = File.ReadAllBytes(path);
        corrupt[0] ^= 255;
        File.WriteAllBytes(path, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TransferAsync(request, Fast, null, CancellationToken.None));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(staged, "COMPLETE.json")));
    }

    [Fact]
    public async Task PublishedBytesAreRereadOnReconcile()
    {
        var request = Package();
        var engine = new FileDeliveryEngine();
        var result = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        var path = Path.Combine(result.DestinationPath, "master.mkv");
        var corrupt = File.ReadAllBytes(path);
        corrupt[^1] ^= 255;
        File.WriteAllBytes(path, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TransferAsync(request, Fast, null, CancellationToken.None));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task CorruptSourceCannotPublish()
    {
        var request = Package();
        var path = Path.Combine(request.SourceDirectory, "master.mkv");
        var changed = File.ReadAllBytes(path);
        changed[0] ^= 255;
        File.WriteAllBytes(path, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(request.DestinationRoot, "collection", request.JobId)));
        Assert.False(File.Exists(Path.Combine(Partial(request), "COMPLETE.json")));
    }

    [Fact]
    public async Task ExistingDifferentPackageIsNeverOverwritten()
    {
        var request = Package();
        var collision = Path.Combine(request.DestinationRoot, "collection", request.JobId);
        Directory.CreateDirectory(collision);
        var path = Path.Combine(collision, "master.mkv");
        File.WriteAllText(path, "existing archive bytes");
        await Assert.ThrowsAsync<IOException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
        Assert.Equal("existing archive bytes", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("C:\\escape")]
    [InlineData("file:stream")]
    [InlineData("CON.txt")]
    [InlineData("COM\u00b9.txt")]
    [InlineData("CONIN$")]
    [InlineData("trailing.")]
    [InlineData("COMPLETE.json")]
    public async Task UnsafeArtifactPathsAreRejected(string relative)
    {
        var request = Package();
        request = request with { Files = [new(relative, 0, new string('0', 64))] };
        await Assert.ThrowsAsync<ArgumentException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
    }

    [Fact]
    public async Task OverlappingRootsAreRejected()
    {
        var request = Package();
        request = request with { DestinationRoot = Path.Combine(request.SourceDirectory, "delivery") };
        await Assert.ThrowsAsync<ArgumentException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
    }

    [Fact]
    public async Task FileSystemRootAlsoCountsAsAnOverlappingAncestor()
    {
        var request = Package();
        request = request with { SourceDirectory = Path.GetPathRoot(temp)! };
        await Assert.ThrowsAsync<ArgumentException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
    }

    [Fact]
    public async Task SourceAndDestinationReparseEscapesAreRejected()
    {
        var request = Package();
        var actual = Path.Combine(temp, "actual");
        Directory.CreateDirectory(actual);
        var link = Path.Combine(temp, "link");
        Directory.CreateSymbolicLink(link, actual);
        await Assert.ThrowsAsync<IOException>(() => new FileDeliveryEngine().TransferAsync(request with { DestinationRoot = Path.Combine(link, "nested") }, Fast, null, CancellationToken.None));
        var mediaPath = Path.Combine(request.SourceDirectory, "master.mkv");
        var actualMedia = Path.Combine(actual, "master.mkv");
        File.Move(mediaPath, actualMedia);
        File.CreateSymbolicLink(mediaPath, actualMedia);
        await Assert.ThrowsAsync<IOException>(() => new FileDeliveryEngine().TransferAsync(request, Fast, null, CancellationToken.None));
        Assert.True(File.Exists(actualMedia));
    }

    [Fact]
    public async Task VerifiedStagingRetainedBeforeRenameCanBeRetried()
    {
        var request = Package();
        var engine = new FileDeliveryEngine();
        var result = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        var staged = Path.Combine(request.DestinationRoot, ".deckcapture-staging", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.JobId))));
        // Simulate restart after COMPLETE was flushed but before atomic rename.
        Directory.Move(result.DestinationPath, staged);
        Assert.True(File.Exists(Path.Combine(staged, "COMPLETE.json")));
        var retried = await engine.TransferAsync(request, Fast, null, CancellationToken.None);
        Assert.Equal(result, retried);
        Assert.False(Directory.Exists(staged));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    public async Task NonPositiveOrNonFiniteSpeedsAreRejected(double transfer, double aggregate)
    {
        var request = Package();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FileDeliveryEngine().TransferAsync(request,
            () => new(transfer, aggregate), null, CancellationToken.None));
    }

    [Fact]
    public async Task AggregateLimitCoversConcurrentCopyAndVerification()
    {
        var first = Package("first");
        var second = Package("second");
        var engine = new FileDeliveryEngine();
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(engine.TransferAsync(first, () => new(100, 1), null, CancellationToken.None),
            engine.TransferAsync(second, () => new(100, 1), null, CancellationToken.None));
        // Two 256-KiB files copied AND reread total one MiB. The generous lower
        // bound tolerates timer variance while catching per-copy-only throttling.
        Assert.True(watch.Elapsed.TotalSeconds >= .8, $"Aggregate copy+verify completed in {watch.Elapsed}.");
    }

    [Fact]
    public async Task ActiveTransferSlowsWhenRateCallbackChanges()
    {
        var request = Package();
        var rates = Fast();
        var watch = Stopwatch.StartNew();
        await new FileDeliveryEngine().TransferAsync(request, () => rates,
            new CallbackProgress(value => { if (value.Phase == "Copying") rates = new(.25, 100); }), CancellationToken.None);
        Assert.True(watch.Elapsed.TotalSeconds >= 1.3, $"Dynamic limit was not applied: {watch.Elapsed}.");
    }

    [Fact]
    public async Task DestinationTamperingBetweenCopyAndVerifyCannotPublish()
    {
        var request = Package();
        var changed = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileDeliveryEngine().TransferAsync(request, Fast,
            new CallbackProgress(value =>
            {
                if (value.Phase != "Verifying" || changed) return;
                changed = true;
                var path = Path.Combine(Partial(request), "master.mkv");
                var bytes = File.ReadAllBytes(path);
                bytes[0] ^= 255;
                File.WriteAllBytes(path, bytes);
            }), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Partial(request), "COMPLETE.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}
