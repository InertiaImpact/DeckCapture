using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed class DevelopmentAccessTests
{
    private const string ValidToken = "a-valid-access-token-with-24-plus-characters";

    [Theory]
    [InlineData("http://127.0.0.1:5278")]
    [InlineData("http://localhost:5278")]
    [InlineData("http://[::1]:5278")]
    [InlineData("http://127.0.0.2:5278;https://localhost:5279")]
    public void DefaultLoopbackRemainsAvailableWithoutAToken(string urls)
        => Assert.False(DevelopmentAccessPolicy.ValidateBinding(urls, null, false));

    [Theory]
    [InlineData("http://0.0.0.0:5278", null)]
    [InlineData("http://capture-host:5278", ValidToken)]
    [InlineData("https://capture-host:5278", null)]
    [InlineData("https://capture-host:5278", "short")]
    [InlineData("http://127.0.0.1:5278;https://capture-host:5279", ValidToken)]
    public void DefaultRemoteBindingStillRequiresHttpsAndALongToken(string urls, string? token)
        => Assert.Throws<InvalidOperationException>(() => DevelopmentAccessPolicy.ValidateBinding(urls, token, false));

    [Fact]
    public void DefaultRemoteHttpsWithConfiguredTokenIsAccepted()
        => Assert.True(DevelopmentAccessPolicy.ValidateBinding("https://capture-host:5278", ValidToken, false));

    [Theory]
    [InlineData("http://0.0.0.0:5278", null)]
    [InlineData("http://capture-host:5278", null)]
    [InlineData("http://[::]:5278", null)]
    [InlineData("http://*:5278", null)]
    [InlineData("http://+:5278", null)]
    [InlineData("http://capture-host:5278", "configured-token")]
    public void ExplicitDevelopmentModePermitsRemoteHttpWithOptionalConfiguredToken(string urls, string? token)
        => Assert.True(DevelopmentAccessPolicy.ValidateBinding(urls, token, true));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://capture-host:5278")]
    [InlineData("http://capture-host:70000")]
    [InlineData("http://capture-host:5278/path")]
    [InlineData("http://capture-host:5278/?token=value")]
    [InlineData("http://capture-host:5278/#fragment")]
    [InlineData("http://user:password@capture-host:5278")]
    [InlineData("http://localhost:5278;")]
    [InlineData("http://localhost:5278\n")]
    public void MalformedOrUnsupportedBindingsAreRejectedEvenWithDevelopmentMode(string urls)
    {
        Assert.Throws<InvalidOperationException>(() => DevelopmentAccessPolicy.ValidateBinding(urls, ValidToken, false));
        Assert.Throws<InvalidOperationException>(() => DevelopmentAccessPolicy.ValidateBinding(urls, null, true));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("capture-host", false)]
    [InlineData("localhost.example.com", false)]
    public void HostChecksClassifyOnlyActualLoopbackHosts(string host, bool expected)
        => Assert.Equal(expected, DevelopmentAccessPolicy.IsLocalHost(host));
}
