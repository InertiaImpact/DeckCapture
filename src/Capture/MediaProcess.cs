using System.Diagnostics;
using System.Text;

namespace DeckCapture.Capture;

// All executable arguments are separate argv entries, including operator-controlled paths.
internal sealed class MediaProcess : IAsyncDisposable
{
    private readonly Queue<string> errors = new();
    private readonly Task stderr;
    private readonly Task stdout;
    private readonly ChildProcessLifetime? lifetime;
    public Process Process { get; }
    public string Error { get { lock (errors) return string.Join(Environment.NewLine, errors); } }

    public MediaProcess(string executable, IEnumerable<string> arguments, Action<string>? output = null, Action<string>? errorOutput = null)
    {
        var start = new ProcessStartInfo(MediaToolPaths.Resolve(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process = new Process { StartInfo = start };
        if (!Process.Start()) throw new IOException($"Could not start {executable}.");
        try { lifetime = ChildProcessLifetime.Attach(Process); }
        catch { Kill(); Process.Dispose(); throw; }
        stderr = Task.Run(async () =>
        {
            while (await Process.StandardError.ReadLineAsync() is { } line)
            {
                lock (errors) { errors.Enqueue(line); while (errors.Count > 100) errors.Dequeue(); }
                errorOutput?.Invoke(line);
            }
        });
        stdout = Task.Run(async () =>
        {
            while (await Process.StandardOutput.ReadLineAsync() is { } line) output?.Invoke(line);
        });
    }

    public async Task<int> WaitAsync(CancellationToken token)
    {
        try { await Process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { Kill(); throw; }
        await Task.WhenAll(stderr, stdout);
        return Process.ExitCode;
    }

    public async Task StopAsync()
    {
        if (Process.HasExited) { await Task.WhenAll(stderr, stdout); return; }
        try { await Process.StandardInput.WriteLineAsync("q"); await Process.StandardInput.FlushAsync(); }
        catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await WaitAsync(timeout.Token); }
        catch (OperationCanceledException) { Kill(); await WaitAsync(CancellationToken.None); }
    }

    public void Kill() { try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync() { Kill(); await Process.WaitForExitAsync(); await Task.WhenAll(stderr, stdout); lifetime?.Dispose(); Process.Dispose(); }

    public static async Task<string> ReadAsync(string executable, IEnumerable<string> args, CancellationToken token)
    {
        var builder = new StringBuilder();
        await using var process = new MediaProcess(executable, args, line => { if (builder.Length < 1_000_000) builder.AppendLine(line); });
        var exit = await process.WaitAsync(token);
        if (exit != 0) throw new IOException($"{Path.GetFileName(executable)} exited {exit}: {process.Error}");
        return builder.ToString();
    }
}
