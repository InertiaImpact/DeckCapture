using System.Net;
using System.Net.Sockets;
using System.Text;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeckCapture.Tests;

public sealed class NotificationTests
{
    private sealed class OutboxFixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "deckcapture-outbox-tests-" + Guid.NewGuid().ToString("N"));
        private readonly EphemeralDataProtectionProvider protection = new();
        internal StateStore Store { get; }
        internal SecretStore Secrets { get; }
        internal NotificationService Service { get; }
        internal OutboxFixture()
        {
            Store = new(DirectoryPath);
            Secrets = new(protection, DirectoryPath);
            Service = Restart();
        }
        internal NotificationService Restart() => new(new StateStore(DirectoryPath), Secrets, NullLogger<NotificationService>.Instance);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }

    // This protocol fixture binds 127.0.0.1 on an OS-selected port and never
    // forwards or resolves recipients. DATA remains only in this test process.
    private sealed class LoopbackRelay : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        private readonly Task serve;
        private readonly bool rejectData;
        private readonly bool closeAfterAcceptance;
        private readonly bool stallGreeting;
        internal readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Commands = [];
        internal readonly List<string> Data = [];
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal Task Completion => serve;

        internal LoopbackRelay(bool rejectData = false, bool closeAfterAcceptance = false, bool stallGreeting = false)
        {
            this.rejectData = rejectData; this.closeAfterAcceptance = closeAfterAcceptance; this.stallGreeting = stallGreeting;
            listener.Start();
            serve = ServeAsync();
        }

        internal NotificationSettings Settings(string username = "") => new()
        {
            Enabled = true, Host = "127.0.0.1", Port = Port, TlsMode = "Relay", Username = username,
            Sender = "capture@deckcapture.invalid", Recipients = ["archive@deckcapture.invalid"]
        };

        private async Task ServeAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                Connected.TrySetResult();
                if (stallGreeting) { await Task.Delay(Timeout.Infinite, lifetime.Token); return; }
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 loopback.invalid SMTP unit-test fixture");
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                {
                    Commands.Add(line);
                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-loopback.invalid");
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                        await writer.WriteLineAsync("250 SIZE 1048576");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                        await writer.WriteLineAsync("250 accepted envelope");
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 send fixture data");
                        while (await reader.ReadLineAsync(lifetime.Token) is { } content && content != ".") Data.Add(content);
                        await writer.WriteLineAsync(rejectData ? "451 synthetic temporary rejection" : "250 fixture accepted");
                        if (!rejectData && closeAfterAcceptance) return;
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("221 closing fixture"); return; }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 reset");
                    else if (line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("535 authentication must never reach this plaintext fixture");
                    else await writer.WriteLineAsync("500 unsupported fixture command");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(2));
            lifetime.Dispose();
        }
    }

    [Fact]
    public void StableOutboxIdentityPersistsAcrossServiceAndStoreRestart()
    {
        using var fixture = new OutboxFixture();
        fixture.Service.Queue("First subject", "Original body", "daily-fixture-20261031");
        fixture.Service.Queue("Duplicate subject", "Must not replace original", "daily-fixture-20261031");
        var restarted = fixture.Restart();
        restarted.Queue("After restart", "Still must not replace original", "daily-fixture-20261031");
        var message = Assert.Single(restarted.Snapshot());
        Assert.Equal("First subject", message.Subject);
        Assert.Equal("Original body", message.Body);
        Assert.Equal("Queued", message.Status);
        message.Status = "Accepted";
        Assert.Equal("Queued", Assert.Single(restarted.Snapshot()).Status);
    }

    [Fact]
    public void IndependentUnkeyedNotificationsAreBothRetained()
    {
        using var fixture = new OutboxFixture();
        fixture.Service.Queue("Same subject", "Same body");
        fixture.Service.Queue("Same subject", "Same body");
        Assert.Equal(2, fixture.Restart().Snapshot().Select(message => message.Id).Distinct().Count());
    }

    [Fact]
    public async Task RelayAcceptancePersistsEvenIfConnectionClosesBeforeQuit()
    {
        using var fixture = new OutboxFixture();
        await using var relay = new LoopbackRelay(closeAfterAcceptance: true);
        fixture.Service.Queue("Fixture notification", "This stays on loopback.", "acceptance-fixture");
        await fixture.Service.ProcessAsync(relay.Settings(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var accepted = Assert.Single(fixture.Service.Snapshot());
        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal(1, accepted.Attempts);
        Assert.Null(accepted.Error);
        Assert.Contains(relay.Data, line => line.Contains("acceptance-fixture@deckcapture.local", StringComparison.Ordinal));
        Assert.Contains("This stays on loopback.", relay.Data);
        var commands = relay.Commands.Count;
        var restarted = fixture.Restart();
        restarted.Queue("Must not replace accepted", "Duplicate", "acceptance-fixture");
        await restarted.ProcessAsync(relay.Settings(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Accepted", Assert.Single(restarted.Snapshot()).Status);
        Assert.Equal(commands, relay.Commands.Count);
    }

    [Fact]
    public async Task TemporaryRelayRejectionSchedulesPersistentRetry()
    {
        using var fixture = new OutboxFixture();
        await using var relay = new LoopbackRelay(rejectData: true);
        fixture.Service.Queue("Retry fixture", "Retain this body", "retry-fixture");
        var before = DateTimeOffset.UtcNow;
        await fixture.Service.ProcessAsync(relay.Settings(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var retry = Assert.Single(fixture.Restart().Snapshot());
        Assert.Equal("RetryScheduled", retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal("Retain this body", retry.Body);
        Assert.NotNull(retry.Error);
        Assert.True(retry.NextAttemptAt >= before.AddSeconds(29));
        // A future retry must return without reconnecting, even if the old relay
        // is no longer accepting a connection.
        await fixture.Restart().ProcessAsync(relay.Settings(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Assert.Single(fixture.Service.Snapshot()).Attempts);
    }

    [Fact]
    public async Task CancellationRetainsQueuedMailAndReleasesOutboxGate()
    {
        using var fixture = new OutboxFixture();
        await using var stalled = new LoopbackRelay(stallGreeting: true);
        fixture.Service.Queue("Canceled connection", "Retain before acceptance", "cancel-fixture");
        using var cancellation = new CancellationTokenSource();
        var processing = fixture.Service.ProcessAsync(stalled.Settings(), cancellation.Token);
        await stalled.Connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = Assert.Single(fixture.Restart().Snapshot());
        Assert.Equal("Queued", retained.Status);
        Assert.Equal(0, retained.Attempts);
        await using var accepting = new LoopbackRelay();
        await fixture.Service.ProcessAsync(accepting.Settings(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Accepted", Assert.Single(fixture.Service.Snapshot()).Status);
    }

    [Fact]
    public async Task PlaintextRelayWithUsernameNeverSendsAuthentication()
    {
        using var fixture = new OutboxFixture();
        await using var relay = new LoopbackRelay();
        fixture.Secrets.SaveSmtp("only-a-unit-test-secret");
        fixture.Service.Queue("Credential refusal fixture", "Do not send.", "plaintext-refusal");
        await fixture.Service.ProcessAsync(relay.Settings("fixture-username"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var retained = Assert.Single(fixture.Restart().Snapshot());
        Assert.Equal("RetryScheduled", retained.Status);
        Assert.Contains("encrypted", retained.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(relay.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(relay.Commands, command => command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(relay.Data);
    }

    [Fact]
    public async Task ExplicitNoLoginRelayIgnoresPreviouslySavedUsernameAndPassword()
    {
        using var fixture = new OutboxFixture();
        await using var relay = new LoopbackRelay();
        fixture.Secrets.SaveSmtp("unused-saved-fixture-password");
        fixture.Service.Queue("Explicit relay", "Loopback fixture only", "explicit-no-login");
        var settings = relay.Settings("previously-saved-username") with { AuthenticationEnabled = false };
        await fixture.Service.ProcessAsync(settings, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await relay.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Accepted", Assert.Single(fixture.Restart().Snapshot()).Status);
        Assert.DoesNotContain(relay.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(relay.Commands, command => command.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisabledNotificationsLeaveOutboxQueuedWithoutConnection()
    {
        using var fixture = new OutboxFixture();
        fixture.Service.Queue("Disabled", "Retain", "disabled-fixture");
        await fixture.Service.ProcessAsync(new() { Enabled = false }, CancellationToken.None);
        Assert.Equal("Queued", Assert.Single(fixture.Restart().Snapshot()).Status);
    }

    [Fact]
    public void RequestIdempotencyRecordPreservesExactResponseAcrossRestartAndCannotBeReplaced()
    {
        using var fixture = new OutboxFixture();
        const string id = "request-'quoted-key";
        const string response = "{\n  \"state\": \"Prepared\",\n  \"id\": \"fixture\"\n}";
        Assert.Null(fixture.Store.ReadRequest(id));
        fixture.Store.SaveRequest(id, "POST /api/recordings/prepare", "original-payload-sha256", 200, response);
        var restarted = new StateStore(fixture.DirectoryPath);
        var saved = restarted.ReadRequest(id)!.Value;
        Assert.Equal("POST /api/recordings/prepare", saved.Path);
        Assert.Equal("original-payload-sha256", saved.Hash);
        Assert.Equal(200, saved.Status);
        Assert.Equal(response, saved.Response);
        Assert.Throws<SqliteException>(() => restarted.SaveRequest(id, "POST /different", "different-hash", 409, "replacement"));
        Assert.Equal(saved, restarted.ReadRequest(id)!.Value);
    }

    [Fact]
    public void FailedBatchDoesNotPersistAnEarlierPartialRecord()
    {
        using var fixture = new OutboxFixture();
        static IEnumerable<(string Kind, string Id, string Json)> InterruptedBatch()
        {
            yield return ("batch-fixture", "partial", "{\"value\":1}");
            throw new InvalidOperationException("Synthetic producer failure before commit");
        }
        Assert.Throws<InvalidOperationException>(() => fixture.Store.SaveBatch(InterruptedBatch()));
        Assert.Empty(new StateStore(fixture.DirectoryPath).ReadAll<System.Text.Json.JsonElement>("batch-fixture"));
    }
}
