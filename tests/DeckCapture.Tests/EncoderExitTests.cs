using DeckCapture.Capture;

namespace DeckCapture.Tests;

public sealed class EncoderExitTests
{
    [Fact]
    public void NativeShutdownCrashReportsItsExitCodeInsteadOfImplyingLastWarningWasTheCause()
    {
        const string warning = "[aist#0:0/pcm_s32le @ 000001e84c6a4dc0] Guessed Channel Layout: stereo";
        var error = SpoolCapture.EncoderExitError(unchecked((int)0xC0000005), unexpectedExit: false, warning);
        Assert.NotNull(error);
        Assert.Contains("-1073741819", error); Assert.Contains("0xC0000005", error); Assert.Contains("access violation", error);
        Assert.Contains("after stop request", error); Assert.Contains("original spool retained", error);
        Assert.Contains("Encoder diagnostics: " + warning, error);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(255, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void RequestedStopNeverTurnsNonzeroExitIntoSuccessAndUnexpectedZeroRemainsFailure(int code, bool unexpected)
    {
        Assert.NotNull(SpoolCapture.EncoderExitError(code, unexpected, ""));
    }

    [Fact]
    public void RequestedCleanExitDoesNotFailBecauseStderrContainsOnlyAChannelLayoutWarning()
    {
        Assert.Null(SpoolCapture.EncoderExitError(0, unexpectedExit: false, "Guessed Channel Layout: stereo"));
    }
}
