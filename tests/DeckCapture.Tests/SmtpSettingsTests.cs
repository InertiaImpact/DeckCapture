using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

public sealed class SmtpSettingsTests
{
    [Fact]
    public void NewDisabledNotificationsDefaultToPlainRelayWithoutAssumingLegacyAuthentication()
    {
        var settings = new NotificationSettings();
        Assert.False(settings.Enabled);
        Assert.Equal("Relay", settings.TlsMode);
        Assert.Equal(25, settings.Port);
        Assert.Null(settings.AuthenticationEnabled);
        Assert.False(settings.UsesAuthentication);
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "deckcapture-smtp-settings-" + Guid.NewGuid().ToString("N"));
        public StateStore Store { get; }
        public SecretStore Secrets { get; }
        public Fixture()
        {
            Store = new(DirectoryPath);
            Secrets = CreateSecrets();
        }
        public SecretStore CreateSecrets() => new(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DirectoryPath, "keys"))), DirectoryPath);
        public NotificationService Service(Func<MailKit.Net.Smtp.SmtpClient>? factory = null) => new(Store, Secrets, NullLogger<NotificationService>.Instance, factory);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(DirectoryPath, true); }
    }

    [Theory]
    [InlineData("Relay", 25, false)]
    [InlineData("StartTls", 587, true)]
    [InlineData("Tls", 465, true)]
    [InlineData("Relay", 2525, false)]
    public void NotificationConfigurationAndProtectedPasswordSurviveStoreAndProtectionRestart(string mode, int port, bool authentication)
    {
        using var fixture = new Fixture();
        const string password = "fixture-password-must-never-be-exported";
        var config = ConfigurationRules.DevelopmentDefaults(fixture.DirectoryPath) with { Notifications = new()
        {
            Enabled = true, Host = "relay.fixture.invalid", Port = port, TlsMode = mode, AuthenticationEnabled = authentication,
            Username = "retained-fixture-user", Sender = "capture@fixture.invalid", Recipients = ["archive@fixture.invalid"], CredentialReference = "legacy-reference"
        } };
        Assert.Empty(ConfigurationRules.Validate(config));
        fixture.Store.Save("configuration", "current", config);
        fixture.Secrets.ChangeSmtp("Replace", password);
        var restored = Assert.Single(new StateStore(fixture.DirectoryPath).ReadAll<AppConfiguration>("configuration"));
        Assert.Equal(mode, restored.Notifications.TlsMode);
        Assert.Equal(port, restored.Notifications.Port);
        Assert.Equal(authentication, restored.Notifications.AuthenticationEnabled);
        Assert.Equal(authentication, restored.Notifications.UsesAuthentication);
        Assert.Equal("retained-fixture-user", restored.Notifications.Username);
        Assert.Equal("legacy-reference", restored.Notifications.CredentialReference);
        var restoredSecrets = fixture.CreateSecrets();
        Assert.Equal(new SmtpSecretStatus(true, "Configured"), restoredSecrets.SmtpStatus());
        Assert.Equal(password, restoredSecrets.ReadSmtp()); // Internal transport read, never an HTTP response.
        var export = JsonSerializer.Serialize(restored, StateStore.Json);
        Assert.DoesNotContain(password, export);
        Assert.DoesNotContain("usesAuthentication", export);
        Assert.DoesNotContain(password, File.ReadAllText(Path.Combine(fixture.DirectoryPath, "smtp.secrets.json")));
        Assert.DoesNotContain(password, JsonSerializer.Serialize(restoredSecrets.SmtpStatus(), StateStore.Json));
    }

    [Fact]
    public void LegacyConfigurationInfersLoginOnlyUntilAnExplicitChoiceIsSaved()
    {
        const string legacy = """{"enabled":true,"host":"relay.fixture.invalid","port":25,"tlsMode":"Relay","username":"old-user","credentialReference":"old-ref"}""";
        var settings = JsonSerializer.Deserialize<NotificationSettings>(legacy, StateStore.Json)!;
        Assert.Null(settings.AuthenticationEnabled);
        Assert.True(settings.UsesAuthentication);
        Assert.False((settings with { AuthenticationEnabled = false }).UsesAuthentication);
        Assert.Equal(25, settings.Port);
        Assert.Equal("Relay", settings.TlsMode);
        using var fixture = new Fixture();
        var oldConfig = ConfigurationRules.DevelopmentDefaults(fixture.DirectoryPath) with { Notifications = settings with { Sender = "capture@fixture.invalid", Recipients = ["archive@fixture.invalid"] } };
        Assert.Empty(ConfigurationRules.Validate(oldConfig)); // Previously accepted settings must not prevent app startup.
    }

    [Fact]
    public void PasswordKeepReplaceClearAndUnreadableStatusNeverReturnSecretValues()
    {
        using var fixture = new Fixture();
        Assert.Equal(new SmtpSecretStatus(false, "Missing"), fixture.Secrets.SmtpStatus());
        Assert.Equal("Configured", fixture.Secrets.ChangeSmtp(null, "legacy-post-value").Status);
        var path = Path.Combine(fixture.DirectoryPath, "smtp.secrets.json");
        var original = File.ReadAllBytes(path);
        Assert.True(fixture.Secrets.ChangeSmtp("Keep", null).SecretConfigured);
        Assert.Equal(original, File.ReadAllBytes(path));
        fixture.Secrets.ChangeSmtp("Replace", "replacement-fixture-value");
        Assert.Equal("replacement-fixture-value", fixture.Secrets.ReadSmtp());
        Assert.Throws<CommandException>(() => fixture.Secrets.ChangeSmtp("Keep", "must-not-be-accepted"));
        Assert.Throws<CommandException>(() => fixture.Secrets.ChangeSmtp("Replace", ""));
        Assert.Throws<CommandException>(() => fixture.Secrets.ChangeSmtp("Clear", "must-not-be-accepted"));
        Assert.False(fixture.Secrets.ChangeSmtp("Clear", null).SecretConfigured);
        Assert.False(File.Exists(path));
        Assert.Equal("Missing", fixture.Secrets.ChangeSmtp("Keep", null).Status);
        File.WriteAllText(path, "damaged-protected-fixture");
        Assert.Equal(new SmtpSecretStatus(false, "Unavailable"), fixture.Secrets.SmtpStatus());
        Assert.Equal("Missing", fixture.Secrets.ChangeSmtp("Clear", null).Status);
    }

    [Theory]
    [InlineData("Relay", SecureSocketOptions.None)]
    [InlineData("StartTls", SecureSocketOptions.StartTls)]
    [InlineData("Tls", SecureSocketOptions.SslOnConnect)]
    public void SecurityModesAreExactAndDoNotSilentlyDowngrade(string mode, SecureSocketOptions expected)
        => Assert.Equal(expected, SmtpTransport.Security(mode));

    [Fact]
    public void ExplicitLoginRequiresUsernameAndAllowsTheSelectedConnectionSecurity()
    {
        using var fixture = new Fixture();
        var config = ConfigurationRules.DevelopmentDefaults(fixture.DirectoryPath) with { Notifications = new()
        {
            Enabled = true, Host = "relay.fixture.invalid", Port = 25, TlsMode = "Relay", AuthenticationEnabled = true,
            Sender = "capture@fixture.invalid", Recipients = ["archive@fixture.invalid"]
        } };
        var errors = ConfigurationRules.Validate(config);
        Assert.Contains(errors, error => error.Contains("username", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("STARTTLS", StringComparison.Ordinal));
        Assert.Empty(ConfigurationRules.Validate(config with { Notifications = config.Notifications with { Username = "fixture-user" } }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task LocalPlainRelayAcceptsExplicitLoginOrNoLogin(bool authentication)
        => ExerciseLocalFixture("Relay", authentication);

    [SmtpTlsFixtureTheory]
    [InlineData("StartTls", true)]
    [InlineData("Tls", true)]
    [InlineData("StartTls", false)]
    [InlineData("Tls", false)]
    public async Task LocalTlsFixtureAcceptsConfiguredLoginOrExplicitNoLogin(string mode, bool authentication)
        => await ExerciseLocalFixture(mode, authentication);

    private static async Task ExerciseLocalFixture(string mode, bool authentication)
    {
        using var fixture = new Fixture();
        await using var relay = new TlsRelay(mode, authentication);
        fixture.Secrets.SaveSmtp(TlsRelay.Password);
        var client = new InspectableSmtpClient
        {
            Timeout = 10_000,
            SslProtocols = SslProtocols.Tls12,
            // Only this loopback fixture's generated certificate is trusted; production keeps MailKit validation.
            ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == relay.Certificate.GetCertHashString()
        };
        var service = fixture.Service(() => client);
        service.Queue("Local TLS fixture", "This message never leaves the test listener.");
        await service.ProcessAsync(relay.Settings(authentication), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        var message = Assert.Single(service.Snapshot());
        Assert.True(message.Status == "Accepted", client.ConnectionError?.ToString() ?? message.Error ?? relay.Failure?.ToString());
        Assert.Equal(authentication, relay.Authenticated);
        Assert.Equal(mode == "StartTls", relay.Commands.Contains("STARTTLS"));
        Assert.Contains("DATA", relay.Commands);
        Assert.Equal("Accepted", Assert.Single(new StateStore(fixture.DirectoryPath).ReadAll<NotificationMessage>("notification")).Status);
    }

    [Theory]
    [InlineData("Relay", false)]
    [InlineData("Relay", true)]
    [InlineData("StartTls", false)]
    [InlineData("StartTls", true)]
    [InlineData("Tls", false)]
    [InlineData("Tls", true)]
    public async Task LoginChoiceIsIndependentFromSecurityAndNoLoginNeverReadsTheStoredPassword(string mode, bool authentication)
    {
        using var fixture = new Fixture();
        fixture.Secrets.SaveSmtp("transport-only-fixture-password");
        if (!authentication) File.WriteAllText(Path.Combine(fixture.DirectoryPath, "smtp.secrets.json"), "unreadable fixture that no-login must not read");
        using var client = new RecordingSmtpClient();
        var settings = new NotificationSettings { Host = "fixture.invalid", Port = 2525, TlsMode = mode, Username = "retained-user", AuthenticationEnabled = authentication };
        await SmtpTransport.ConnectAsync(client, settings, fixture.Secrets, CancellationToken.None);
        Assert.Equal(SmtpTransport.Security(mode), client.ConnectionSecurity);
        Assert.Equal(authentication, client.LoginUsername is not null);
        if (authentication) { Assert.Equal("retained-user", client.LoginUsername); Assert.Equal("transport-only-fixture-password", client.LoginPassword); }
    }

    [Fact]
    public async Task SupervisorSaveAndNewSupervisorInstanceReturnTheExactNotificationSettings()
    {
        using var fixture = new Fixture();
        CaptureSupervisor CreateSupervisor() => new(new StateStore(fixture.DirectoryPath), new UnusedCapture(), new UnusedDelivery(),
            fixture.Service(), NullLogger<CaptureSupervisor>.Instance, fixture.DirectoryPath);
        var supervisor = CreateSupervisor();
        try
        {
            var config = await supervisor.ConfigurationAsync();
            var notification = new NotificationSettings { Enabled = true, Host = "relay.fixture.invalid", Port = 25, TlsMode = "Relay", AuthenticationEnabled = false,
                Username = "retained-but-disabled-login", Sender = "capture@fixture.invalid", Recipients = ["archive@fixture.invalid"] };
            var saved = await supervisor.UpdateConfigurationAsync(config with { Notifications = notification }, CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(notification, StateStore.Json), JsonSerializer.Serialize(saved.Notifications, StateStore.Json));
        }
        finally { await supervisor.StopAsync(CancellationToken.None); supervisor.Dispose(); }
        var restarted = CreateSupervisor();
        try
        {
            var restored = (await restarted.ConfigurationAsync()).Notifications;
            Assert.True(restored.Enabled); Assert.Equal("relay.fixture.invalid", restored.Host); Assert.Equal(25, restored.Port);
            Assert.Equal("Relay", restored.TlsMode); Assert.False(restored.AuthenticationEnabled); Assert.False(restored.UsesAuthentication);
            Assert.Equal("retained-but-disabled-login", restored.Username);
        }
        finally { await restarted.StopAsync(CancellationToken.None); restarted.Dispose(); }
    }

    private sealed class RecordingSmtpClient : MailKit.Net.Smtp.SmtpClient
    {
        public SecureSocketOptions? ConnectionSecurity { get; private set; }
        public string? LoginUsername { get; private set; }
        public string? LoginPassword { get; private set; }
        public override Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        { ConnectionSecurity = options; return Task.CompletedTask; }
        public override Task AuthenticateAsync(Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default)
        {
            var credential = credentials.GetCredential(new Uri("smtp://fixture.invalid"), "PLAIN")!;
            LoginUsername = credential.UserName; LoginPassword = credential.Password; return Task.CompletedTask;
        }
    }
    private sealed class UnusedCapture : ICaptureBackend
    {
        public Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken) => Task.FromResult(new CaptureCapabilities(true, false, "Explicit SMTP settings test fixture"));
        public Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken) => throw new InvalidOperationException("Settings persistence tests must not open inputs.");
    }
    private sealed class UnusedDelivery : IFileDelivery
    {
        public Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates, IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Settings persistence tests must not deliver media.");
    }

    private sealed class InspectableSmtpClient : MailKit.Net.Smtp.SmtpClient
    {
        public Exception? ConnectionError { get; private set; }
        public override async Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            try { await base.ConnectAsync(host, port, options, cancellationToken); }
            catch (Exception error) { ConnectionError = error; throw; }
        }
    }

    // SMTP/TLS fixture accepts only 127.0.0.1 and stores DATA in memory. It never forwards or resolves recipients.
    private sealed class TlsRelay : IAsyncDisposable
    {
        public const string Password = "localhost-fixture-password";
        private const string Username = "localhost-fixture-user";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(25));
        private readonly string mode;
        private readonly bool requiresAuthentication;
        public X509Certificate2 Certificate { get; }
        public List<string> Commands { get; } = [];
        public bool Authenticated { get; private set; }
        public Exception? Failure { get; private set; }
        public Task Completion { get; }

        public TlsRelay(string mode, bool requiresAuthentication)
        {
            this.mode = mode; this.requiresAuthentication = requiresAuthentication;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            listener.Start(); Completion = ServeAsync();
        }
        public NotificationSettings Settings(bool authentication) => new()
        {
            Enabled = true, Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TlsMode = mode,
            Username = Username, AuthenticationEnabled = authentication, Sender = "capture@fixture.invalid", Recipients = ["archive@fixture.invalid"]
        };
        private async Task<SslStream> EncryptAsync(Stream stream)
        {
            var encrypted = new SslStream(stream, leaveInnerStreamOpen: false);
            await encrypted.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate, EnabledSslProtocols = SslProtocols.Tls12 }, lifetime.Token);
            return encrypted;
        }
        private static StreamReader Reader(Stream stream) => new(stream, Encoding.ASCII, leaveOpen: true);
        private static StreamWriter Writer(Stream stream) => new(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        private async Task ServeAsync()
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync(lifetime.Token);
                Stream stream = connection.GetStream();
                var encrypted = mode == "Tls";
                if (encrypted) stream = await EncryptAsync(stream);
                var reader = Reader(stream); var writer = Writer(stream);
                try
                {
                    await writer.WriteLineAsync("220 localhost.invalid SMTP TLS fixture");
                    while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                    {
                        var command = line.Split(' ', 2)[0].ToUpperInvariant(); Commands.Add(command);
                        if (command is "EHLO" or "HELO")
                        {
                            await writer.WriteLineAsync("250-localhost.invalid");
                            await writer.WriteLineAsync(mode == "StartTls" && !encrypted ? "250-STARTTLS" : "250-AUTH PLAIN");
                            await writer.WriteLineAsync("250 SIZE 1048576");
                        }
                        else if (command == "STARTTLS" && !encrypted)
                        {
                            await writer.WriteLineAsync("220 begin TLS");
                            reader.Dispose(); await writer.DisposeAsync();
                            stream = await EncryptAsync(stream); reader = Reader(stream); writer = Writer(stream); encrypted = true;
                        }
                        else if (command == "AUTH" && (encrypted || mode == "Relay"))
                        {
                            var pieces = line.Split(' ', 3);
                            var encoded = pieces.Length == 3 ? pieces[2] : null;
                            if (encoded is null) { await writer.WriteLineAsync("334 "); encoded = await reader.ReadLineAsync(lifetime.Token); }
                            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encoded!)).Split('\0');
                            Authenticated = credentials.Length >= 3 && credentials[^2] == Username && credentials[^1] == Password;
                            await writer.WriteLineAsync(Authenticated ? "235 authenticated" : "535 invalid fixture login");
                        }
                        else if (command is "MAIL" or "RCPT") await writer.WriteLineAsync(requiresAuthentication && !Authenticated ? "530 authentication required" : "250 accepted");
                        else if (command == "DATA")
                        {
                            await writer.WriteLineAsync("354 fixture data");
                            while (await reader.ReadLineAsync(lifetime.Token) is { } data && data != ".") { }
                            await writer.WriteLineAsync("250 fixture accepted");
                        }
                        else if (command == "QUIT") { await writer.WriteLineAsync("221 closing"); return; }
                        else if (command == "RSET") await writer.WriteLineAsync("250 reset");
                        else await writer.WriteLineAsync("500 unsupported fixture command");
                    }
                }
                finally { reader.Dispose(); await writer.DisposeAsync(); await stream.DisposeAsync(); }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
            catch (IOException error) { Failure = error; }
            catch (System.Security.Authentication.AuthenticationException error) { Failure = error; }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop();
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(3)); } finally { Certificate.Dispose(); lifetime.Dispose(); }
        }
    }
}

public sealed class SmtpTlsFixtureTheoryAttribute : TheoryAttribute
{
    public SmtpTlsFixtureTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DECKCAPTURE_SMTP_TLS_INTEGRATION") != "1")
            Skip = "Set DECKCAPTURE_SMTP_TLS_INTEGRATION=1 on a host that permits Windows/OpenSSL TLS credentials; uses loopback only.";
    }
}
