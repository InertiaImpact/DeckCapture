using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeckCapture.Domain;

namespace DeckCapture.Delivery;

public sealed class FileDeliveryEngine : IFileDelivery
{
    private const string CompleteName = "COMPLETE.json";
    private const string OwnerName = ".DELIVERY.json";
    private readonly TransferLimiter limiter = new();

    private sealed record Manifest(string JobId, ArtifactFile[] Files);
    private sealed record Owner(string SourceDirectory, Manifest Manifest);

    public async Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates,
        IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rates);
        cancellationToken.ThrowIfCancellationRequested();
        var initialRates = rates();
        if (!double.IsFinite(initialRates.PerTransferMiBps) || initialRates.PerTransferMiBps <= 0 ||
            !double.IsFinite(initialRates.AggregateMiBps) || initialRates.AggregateMiBps <= 0)
            throw new ArgumentOutOfRangeException(nameof(rates), "Transfer and aggregate speeds must be finite and positive.");
        var source = DeliveryPaths.Root(request.SourceDirectory);
        var root = DeliveryPaths.Root(request.DestinationRoot);
        if (DeliveryPaths.Contains(source, root) || DeliveryPaths.Contains(root, source))
            throw new ArgumentException("Source and destination roots must not overlap.");
        var id = DeliveryPaths.Relative(request.JobId);
        if (id.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("Job ID must be one path segment.");
        var relative = DeliveryPaths.Relative(request.RelativeDirectory, true);
        if (relative.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(".deckcapture-staging", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The private staging name is reserved.");
        var files = ValidateFiles(request.Files);
        var manifest = new Manifest(request.JobId, files);
        var manifestJson = JsonSerializer.Serialize(manifest);
        var ownerJson = JsonSerializer.Serialize(new Owner(source, manifest));
        var destinationParent = DeliveryPaths.Under(root, relative);
        var destination = DeliveryPaths.Under(destinationParent, id);
        var privateRoot = DeliveryPaths.Under(root, ".deckcapture-staging");
        DeliveryPaths.CreateDirectory(privateRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.JobId)));
        var staging = DeliveryPaths.Under(privateRoot, key);
        // FileShare.None prevents two in-process or restarted workers from touching
        // the same retained partial. The lock is outside the directory being moved.
        await using var lease = new FileStream(DeliveryPaths.Under(privateRoot, key + ".lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        var bucket = new TransferLimiter.Bucket();
        long copied = 0;
        var total = files.Sum(f => f.Size);
        var elapsed = Stopwatch.StartNew();
        void Report(string phase) => progress?.Report(new(phase, copied, total,
            copied / Math.Max(elapsed.Elapsed.TotalSeconds, .001) / 1048576));

        // Reconcile a publication that succeeded before the supervisor persisted
        // success. A name collision is never permission to overwrite anything.
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            await RequireTextAsync(DeliveryPaths.Under(destination, CompleteName), manifestJson, cancellationToken);
            RequireInventory(destination, files, true);
            foreach (var file in files)
            {
                await VerifySourceAsync(source, file, cancellationToken);
                await VerifyDestinationAsync(destination, file, bucket, rates, cancellationToken);
                copied += file.Size;
                Report("Verifying");
            }
            return new(destination, true);
        }

        if (Directory.Exists(staging))
            await RequireTextAsync(DeliveryPaths.Under(staging, OwnerName), ownerJson, cancellationToken);
        else
        {
            DeliveryPaths.CreateDirectory(staging);
            await WriteNewAsync(DeliveryPaths.Under(staging, OwnerName), ownerJson, cancellationToken);
        }
        var retainedComplete = DeliveryPaths.Under(staging, CompleteName);
        if (File.Exists(retainedComplete)) await RequireTextAsync(retainedComplete, manifestJson, cancellationToken);
        RequireInventory(staging, files, File.Exists(retainedComplete));

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = DeliveryPaths.Under(source, file.RelativePath);
            var targetFile = DeliveryPaths.Under(staging, file.RelativePath);
            Report("VerifyingSource");
            // Keep a read-only sharing handle across source validation and copy.
            // On Windows no writer can change these bytes while this handle lives.
            await using var input = OpenRead(sourceFile);
            await RequireHashAsync(input, file, null, cancellationToken);
            input.Position = 0;
            DeliveryPaths.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            DeliveryPaths.NoLinks(targetFile);
            await using (var output = new FileStream(targetFile, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, TransferLimiter.ChunkSize, FileOptions.Asynchronous))
            {
                if (output.Length > file.Size) throw new InvalidDataException("Retained partial is longer than its source; preserved for review.");
                var prefix = output.Length;
                await VerifyPrefixAsync(input, output, prefix, bucket, rates, cancellationToken);
                copied += prefix;
                var buffer = new byte[TransferLimiter.ChunkSize];
                while (input.Position < file.Size)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Size - input.Position)), cancellationToken);
                    if (count == 0) throw new EndOfStreamException("Source changed while delivering.");
                    await limiter.TakeAsync(bucket, count, rates, cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    copied += count;
                    Report("Copying");
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
        }

        // Verification is a fresh destination read and shares the same bandwidth
        // buckets as copying and resume reads. COMPLETE is never used as a shortcut.
        foreach (var file in files)
        {
            Report("Verifying");
            await VerifyDestinationAsync(staging, file, bucket, rates, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var completePath = DeliveryPaths.Under(staging, CompleteName);
        if (File.Exists(completePath)) await RequireTextAsync(completePath, manifestJson, cancellationToken);
        else await WriteNewAsync(completePath, manifestJson, cancellationToken);
        RequireInventory(staging, files, true);
        DeliveryPaths.CreateDirectory(destinationParent);
        DeliveryPaths.NoLinks(staging);
        DeliveryPaths.NoLinks(destination);
        cancellationToken.ThrowIfCancellationRequested();
        // Same-root rename is the publication boundary. No overwrite or fallback
        // copy is allowed. Failure retains the fully verified staging package.
        Directory.Move(staging, destination);
        Report("Delivered");
        return new(destination, true);
    }

    private static ArtifactFile[] ValidateFiles(IReadOnlyList<ArtifactFile> files)
    {
        if (files.Count == 0) throw new ArgumentException("A delivery package must contain files.");
        var result = new List<ArtifactFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = DeliveryPaths.Relative(file.RelativePath);
            if (path.Split(Path.DirectorySeparatorChar).Any(p => p.Equals(CompleteName, StringComparison.OrdinalIgnoreCase) || p.Equals(OwnerName, StringComparison.OrdinalIgnoreCase)) ||
                !names.Add(path) || file.Size < 0 || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("Invalid or duplicate delivery artifact.");
            result.Add(file with { RelativePath = path, Sha256 = file.Sha256.ToUpperInvariant() });
        }
        foreach (var name in names)
            if (names.Any(other => !other.Equals(name, StringComparison.OrdinalIgnoreCase) && other.StartsWith(name + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An artifact path is also a parent directory.");
        _ = checked(result.Sum(f => f.Size));
        return result.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static FileStream OpenRead(string path)
    {
        DeliveryPaths.NoLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferLimiter.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task VerifySourceAsync(string root, ArtifactFile file, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(DeliveryPaths.Under(root, file.RelativePath));
        await RequireHashAsync(stream, file, null, cancellationToken);
    }

    private async Task VerifyDestinationAsync(string root, ArtifactFile file, TransferLimiter.Bucket bucket,
        Func<DeliveryRates> rates, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(DeliveryPaths.Under(root, file.RelativePath));
        await RequireHashAsync(stream, file, (count, token) => limiter.TakeAsync(bucket, count, rates, token), cancellationToken);
    }

    private static async Task RequireHashAsync(FileStream stream, ArtifactFile file,
        Func<int, CancellationToken, Task>? throttle, CancellationToken cancellationToken)
    {
        if (stream.Length != file.Size) throw new InvalidDataException($"Artifact length mismatch: {file.RelativePath}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[TransferLimiter.ChunkSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reserve before I/O so verification reads cannot bypass the limiter.
            var expected = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
            if (expected == 0) break;
            if (throttle is not null) await throttle(expected, cancellationToken);
            var count = await stream.ReadAsync(buffer.AsMemory(0, expected), cancellationToken);
            if (count == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, count);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch: {file.RelativePath}; retained bytes were preserved.");
    }

    private async Task VerifyPrefixAsync(FileStream source, FileStream destination, long prefix, TransferLimiter.Bucket bucket,
        Func<DeliveryRates> rates, CancellationToken cancellationToken)
    {
        var sourceBuffer = new byte[TransferLimiter.ChunkSize];
        var destinationBuffer = new byte[TransferLimiter.ChunkSize];
        while (destination.Position < prefix)
        {
            var count = (int)Math.Min(sourceBuffer.Length, prefix - destination.Position);
            await limiter.TakeAsync(bucket, count, rates, cancellationToken);
            await source.ReadExactlyAsync(sourceBuffer.AsMemory(0, count), cancellationToken);
            await destination.ReadExactlyAsync(destinationBuffer.AsMemory(0, count), cancellationToken);
            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(destinationBuffer.AsSpan(0, count)))
                throw new InvalidDataException("Retained partial prefix differs from source; preserved for review.");
        }
    }

    private static async Task RequireTextAsync(string path, string expected, CancellationToken cancellationToken)
    {
        DeliveryPaths.NoLinks(path);
        if (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path, cancellationToken), expected, StringComparison.Ordinal))
            throw new IOException("Package identity collision or incomplete ownership metadata; nothing was overwritten.");
    }

    private static async Task WriteNewAsync(string path, string text, CancellationToken cancellationToken)
    {
        DeliveryPaths.NoLinks(path);
        // Finish each small marker without cancellation after this initial check.
        // A torn marker causes a safe collision failure on restart, never delivery.
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None);
        stream.Flush(true);
    }

    private static void RequireInventory(string root, ArtifactFile[] files, bool complete)
    {
        var allowed = files.Select(f => Path.GetFullPath(Path.Combine(root, f.RelativePath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowed.Add(Path.Combine(root, OwnerName));
        if (complete) allowed.Add(Path.Combine(root, CompleteName));
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var directory))
        {
            DeliveryPaths.NoLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                DeliveryPaths.NoLinks(entry);
                if (Directory.Exists(entry)) queue.Enqueue(entry);
                else if (!allowed.Contains(entry)) throw new IOException("Package contains unexpected files; retained for review.");
            }
        }
    }
}
