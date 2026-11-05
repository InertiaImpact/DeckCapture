using System.Text;
using System.Text.RegularExpressions;
using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed record LivePreviewFile(byte[] Content, string ContentType);

public sealed partial class CaptureSupervisor
{
    public async Task<LivePreviewFile?> ReadLivePreviewAsync(string channelId, string jobId, string fileName, CancellationToken token)
    {
        string? manifest;
        string cacheRoot;
        await _gate.WaitAsync(token);
        try
        {
            if (_jobs.TryGetValue(jobId, out var job) && job.ChannelId == channelId && OwnsInputUnderGate(job) &&
                _live.TryGetValue(jobId, out var live) && !live.Session.Completion.IsCompleted)
            { manifest = live.Session.Metrics.LivePreviewPath; cacheRoot = job.Configuration.CacheRoot; }
            else if (_monitors.TryGetValue(channelId, out var monitor) && monitor.Id == jobId && !monitor.Closing &&
                monitor.Session is { } session && !session.Completion.IsCompleted)
            { manifest = session.Metrics.LivePreviewPath; cacheRoot = monitor.CacheRoot; }
            else return null;
        }
        finally { _gate.Release(); }
        if (manifest is null) return null;
        var result = await LivePreviewFiles.ReadAsync(cacheRoot, jobId, manifest, fileName, token);
        // A completed/closed/replaced session cannot keep serving files because a read was in flight.
        await _gate.WaitAsync(token);
        try { return (_live.ContainsKey(jobId) && _jobs.TryGetValue(jobId, out var current) && OwnsInputUnderGate(current)) ||
            (_monitors.TryGetValue(channelId, out var monitor) && monitor.Id == jobId && !monitor.Closing && monitor.Session is { } session && !session.Completion.IsCompleted) ? result : null; }
        finally { _gate.Release(); }
    }

    private CaptureMetrics PublicMetricsUnderGate(string channelId)
    {
        var owner = MonitoringUnderGate(channelId);
        var metrics = new CaptureMetrics();
        // A newly opening owner must never inherit fresh metrics from a retiring session.
        if (owner?.Source == "RecordingJob" && _live.TryGetValue(owner.Id, out var live) && !live.Session.Completion.IsCompleted) metrics = live.Session.Metrics;
        else if (owner?.Source == "Monitor" && _monitors.TryGetValue(channelId, out var monitor) &&
            monitor.Id == owner.Id && !monitor.Closing && monitor.Session is { } session && !session.Completion.IsCompleted)
            metrics = session.Metrics;
        var epoch = owner?.Id;
        return metrics with { LivePreviewPath = null, LivePreviewUrl = epoch is null || metrics.LivePreviewPath is null ? null
            : $"/api/channels/{Uri.EscapeDataString(channelId)}/live/{epoch}/index.m3u8" };
    }
}

internal static class LivePreviewFiles
{
    internal static string? ResolveJpeg(string cacheRoot, string epoch, string path)
    {
        if (!IsJobId(epoch) || !Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "preview.jpg") return null;
        try
        {
            var root = PathSafety.UnderRoot(cacheRoot, Path.Combine(".spool", epoch));
            var spool = Directory.GetParent(Path.GetFullPath(path));
            if (spool?.Parent is null || !Regex.IsMatch(spool.Name, @"^spool-[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ||
                !spool.Parent.FullName.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            return PathSafety.UnderRoot(root, Path.Combine(spool.Name, "preview.jpg"));
        }
        catch (Exception exception) when (exception is CommandException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    internal static bool IsSegment(string value) => Regex.IsMatch(value, @"^index\d{1,9}\.ts$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool IsJobId(string value) => Regex.IsMatch(value, @"^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static string? Resolve(string cacheRoot, string jobId, string manifest, string fileName)
    {
        if (!IsJobId(jobId) || (fileName != "index.m3u8" && !IsSegment(fileName)) || !Path.IsPathFullyQualified(manifest)) return null;
        try
        {
            var jobRoot = PathSafety.UnderRoot(cacheRoot, Path.Combine(".spool", jobId));
            var preview = Directory.GetParent(Path.GetFullPath(manifest));
            var spool = preview?.Parent;
            if (Path.GetFileName(manifest) != "index.m3u8" || preview?.Name != "preview-hls" || spool?.Parent is null ||
                !Regex.IsMatch(spool.Name, @"^spool-[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ||
                !spool.Parent.FullName.Equals(jobRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            return PathSafety.UnderRoot(jobRoot, Path.Combine(spool.Name, "preview-hls", fileName));
        }
        catch (Exception exception) when (exception is CommandException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    internal static async Task<LivePreviewFile?> ReadAsync(string cacheRoot, string jobId, string manifest, string fileName, CancellationToken token)
    {
        var path = Resolve(cacheRoot, jobId, manifest, fileName);
        if (path is null) return null;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, true);
            var maximum = fileName == "index.m3u8" ? 32768 : 2 * 1024 * 1024;
            if (input.Length is <= 0 || input.Length > maximum) return null;
            var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
            if (fileName == "index.m3u8" && !ValidManifest(Encoding.UTF8.GetString(bytes))) return null;
            return new(bytes, fileName == "index.m3u8" ? "application/vnd.apple.mpegurl" : "video/mp2t");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static bool ValidManifest(string text)
    {
        if (!text.StartsWith("#EXTM3U", StringComparison.Ordinal) || text.Length > 32768 || text.Contains("URI=", StringComparison.OrdinalIgnoreCase)) return false;
        var count = 0;
        var lines = text.Split('\n'); if (lines.Length > 128) return false;
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!IsSegment(line) || ++count > 6) return false;
        }
        return count > 0;
    }
}
