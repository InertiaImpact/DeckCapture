using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DeckCapture.Capture;

// Diagnostic persistence must never block the protocol reader or preservation writer.
// The bounded queue is authoritative; disk failures leave the capture running and logs best-effort.
internal sealed class WorkerLogTail : IAsyncDisposable
{
    private readonly string path;
    private readonly Queue<string> lines = new();
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task writer;
    private int disposed;

    public WorkerLogTail(string path)
    {
        this.path = path;
        writer = PersistAsync();
    }
    public void Append(string line, bool alreadySanitized = false)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var value = alreadySanitized ? line : Sanitize(line);
        if (value.Length > 2048) value = value[..2047] + "…";
        lock (lines) { lines.Enqueue(value); while (lines.Count > 200) lines.Dequeue(); }
        changed.Writer.TryWrite(true);
    }
    internal static string Sanitize(string value)
    {
        // Windows paths may contain spaces; retain the error prefix while removing the path-bearing remainder.
        var redacted = Regex.Replace(value, @"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""<>|]*", "[redacted path]", RegexOptions.CultureInvariant);
        return Regex.Replace(redacted, @"\b(password|token|secret|authorization)\s*[:=]\s*\S+", "$1=[redacted]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    internal string RecentText()
    {
        lock (lines)
        {
            var tail = string.Join(Environment.NewLine, lines.TakeLast(8));
            return tail.Length <= 2048 ? tail : tail[^2048..];
        }
    }
    private async Task PersistAsync()
    {
        try
        {
            while (await changed.Reader.WaitToReadAsync(stop.Token))
            {
                while (changed.Reader.TryRead(out _)) { }
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                await FlushAsync();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private async Task FlushAsync()
    {
        string[] snapshot;
        lock (lines) snapshot = lines.ToArray();
        try
        {
            var temporary = path + ".next";
            await File.WriteAllLinesAsync(temporary, snapshot);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Logging is best-effort, including on a full cache volume. */ }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        await writer;
        await FlushAsync();
        stop.Dispose();
    }
}
