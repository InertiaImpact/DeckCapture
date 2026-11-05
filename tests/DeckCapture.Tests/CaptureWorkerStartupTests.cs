using DeckCapture.Capture;
using DeckCapture.Domain;
using Xunit;

namespace DeckCapture.Tests;

public sealed class CaptureWorkerStartupTests
{
    [Theory]
    [InlineData("[in#0 @ 0000026adc78e2c0] Could not set format code ntsc for device81:d016aa52:00000000")]
    [InlineData("[in#0 @ 0000026adc78e2c0] Cannot Autodetect input stream or No signal\r\nError opening input: I/O error")]
    public async Task FailedCompletionBeforeReadyImmediatelyReportsActualInputFailure(string diagnostic)
    {
        var lifecycle = new CaptureWorkerLifecycle();
        var result = new CaptureResult(false, null, 0, 0, 0, "Input encoder exited: " + diagnostic);
        lifecycle.Receive(new("complete", Result: result), "unrelated stderr");
        var failure = await Assert.ThrowsAsync<IOException>(() => lifecycle.Ready);
        Assert.Contains(result.Error!, failure.Message);
        Assert.Equal(result, await lifecycle.Completion);
        lifecycle.Exited(1, new(), "generic exit detail");
        lifecycle.Receive(new("ready"), "");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => lifecycle.Ready));
        Assert.Equal(result, await lifecycle.Completion);
    }

    [Fact]
    public async Task SuccessfulCompletionBeforeReadyFailsStartupWithoutReplacingFinalResult()
    {
        var lifecycle = new CaptureWorkerLifecycle();
        var result = new CaptureResult(true, "retained.mkv", 5, 125, 0);
        lifecycle.Receive(new("complete", Result: result), "");
        Assert.Contains("completed before its input became ready", (await Assert.ThrowsAsync<IOException>(() => lifecycle.Ready)).Message);
        lifecycle.Exited(0, new(), "");
        Assert.Equal(result, await lifecycle.Completion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletionAfterReadyPreservesNormalCaptureBehavior(bool success)
    {
        var lifecycle = new CaptureWorkerLifecycle();
        lifecycle.Receive(new("ready"), "");
        await lifecycle.Ready;
        var result = new CaptureResult(success, "retained.mkv", 6, 150, 2, success ? null : "Encoder failed");
        lifecycle.Receive(new("complete", Result: result), "");
        lifecycle.Exited(success ? 0 : 1, new(), "");
        Assert.True(lifecycle.Ready.IsCompletedSuccessfully);
        Assert.Equal(result, await lifecycle.Completion);
    }

    [Fact]
    public async Task FallbackUsesBoundedRedactedRecentStderrWhenNoResultArrives()
    {
        var directory = NewDirectory();
        await using var log = new WorkerLogTail(Path.Combine(directory, "stderr.log"));
        for (var i = 0; i < 220; i++) log.Append("old diagnostic " + i);
        log.Append("Recent failure token=private-token at C:\\private\\settings.json");
        var lifecycle = new CaptureWorkerLifecycle();
        lifecycle.Exited(1, new() { Frames = 17 }, log.RecentText());
        var failure = await Assert.ThrowsAsync<IOException>(() => lifecycle.Ready);
        Assert.Contains("Recent failure", failure.Message);
        Assert.DoesNotContain("private-token", failure.Message);
        Assert.DoesNotContain("settings.json", failure.Message);
        Assert.DoesNotContain("old diagnostic 0", failure.Message);
        Assert.True(failure.Message.Length < 2200);
        Assert.Equal(17, (await lifecycle.Completion).Frames);
    }

    [Fact]
    public async Task ExplicitErrorIsSanitizedAndTakesPrecedenceOverFallbacks()
    {
        var lifecycle = new CaptureWorkerLifecycle();
        lifecycle.Receive(new("complete", Result: new(false, null, 0, 0, 0, "Input failed token=private-token at C:\\private\\file"), Error: "event fallback"), "stderr fallback");
        var failure = await Assert.ThrowsAsync<IOException>(() => lifecycle.Ready);
        Assert.Contains("Input failed", failure.Message);
        Assert.DoesNotContain("private", failure.Message);
        Assert.DoesNotContain("fallback", failure.Message);
        Assert.DoesNotContain("private", (await lifecycle.Completion).Error!);
    }

    [Fact]
    public async Task RealWorkerStartupFailureReachesReadyBeforeGenericProcessExit()
    {
        var options = new CaptureOpenOptions(new() { SyntheticPattern = "invalid-startup-fixture" }, new() { LivePreviewEnabled = false }, NewDirectory())
            { LivePreviewPreflightCompleted = true };
        await using var session = new CaptureSession(options);
        var failure = await Assert.ThrowsAsync<IOException>(() => session.Ready.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("SyntheticPattern must be testsrc2 or black", failure.Message);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Contains("SyntheticPattern must be testsrc2 or black", result.Error!);
    }

    [Fact]
    public async Task StopDrainsDiagnosticsEvenWhenChildHasAlreadyExited()
    {
        var optionsPath = Path.Combine(NewDirectory(), "invalid-options.json");
        await File.WriteAllTextAsync(optionsPath, "{invalid-json");
        var path = CaptureBackend.ResolveWorkerPath();
        var dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = false;
        await using var process = new MediaProcess(dll ? "dotnet" : path, dll ? [path, optionsPath] : [optionsPath], errorOutput: _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            drained = true;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.Process.WaitForExitAsync(timeout.Token);
            var stopping = process.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            await stopping.WaitAsync(timeout.Token);
            Assert.True(drained);
            Assert.Contains("Json", process.Error);
        }
        finally { release.TrySetResult(); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Environment.CurrentDirectory, ".local", "startup-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
