using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.Extensions.Logging;

namespace DeckCapture.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "deckcapture-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
    private static DiagnosticsLimits SmallLimits() => new(FileBytes: 4096, FileCount: 3, MemoryEntries: 5,
        EntryCharacters: 512, EvidenceBytes: 1024, BundleJobs: 2);
    private string State => Path.Combine(temp, "state");

    [Fact]
    public void VerbosityIsDisabledByDefaultAndPersistsIndependentlyOfConfiguration()
    {
        Directory.CreateDirectory(State);
        var config = Path.Combine(State, "configuration-sentinel.json");
        File.WriteAllText(config, "Do not alter recording configuration");
        using (var diagnostics = new DevelopmentDiagnostics(State))
        {
            Assert.False(diagnostics.GetStatus().Enabled);
            var logger = diagnostics.CreateLogger("DeckCapture.Tests");
            logger.LogDebug("Suppressed debug");
            logger.LogInformation("Basic action");
            logger.LogError("Always retained error");
            Assert.Equal(2, diagnostics.ReadRecent().Count);
            Assert.True(diagnostics.SetEnabled(true).Enabled);
            logger.LogTrace("Trace after enabling");
            Assert.Contains(diagnostics.ReadRecent(), entry => entry.Message == "Trace after enabling");
        }
        using (var restarted = new DevelopmentDiagnostics(State))
        {
            Assert.True(restarted.GetStatus().Enabled);
            Assert.Contains(restarted.ReadRecent(), entry => entry.Message == "Always retained error");
            restarted.SetEnabled(false);
        }
        using var disabled = new DevelopmentDiagnostics(State);
        Assert.False(disabled.GetStatus().Enabled);
        Assert.Equal("Do not alter recording configuration", File.ReadAllText(config));
    }

    [Fact]
    public async Task RotationAndRestartKeepDiskAndMemoryBounded()
    {
        using (var diagnostics = new DevelopmentDiagnostics(State, limits: SmallLimits()))
        {
            var logger = diagnostics.CreateLogger("DeckCapture.Tests");
            for (var index = 0; index < 200; index++) logger.LogInformation("row {Index}: {Payload}", index, new string('x', 300));
            Assert.Equal(5, diagnostics.ReadRecent(2000).Count);
            Assert.Contains("row 199:", diagnostics.ReadRecent()[0].Message);
            var files = diagnostics.GetLogFiles();
            Assert.InRange(files.Count, 1, 3);
            Assert.All(files, file => Assert.InRange(file.Bytes, 1, 4096));
            Assert.Equal(3, Directory.GetFiles(Path.Combine(State, "logs")).Length);
            foreach (var file in files)
            {
                var text = Encoding.UTF8.GetString((await diagnostics.ReadLogAsync(file.Id)).Content);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Assert.NotNull(JsonSerializer.Deserialize<DiagnosticsEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        using var restarted = new DevelopmentDiagnostics(State, limits: SmallLimits());
        Assert.Equal(5, restarted.ReadRecent().Count);
        Assert.Contains("row 199:", restarted.ReadRecent()[0].Message);
    }

    [Fact]
    public async Task SecretsAreRedactedBeforePersistenceAndDownloadsRemainValidJson()
    {
        const string token = "private-access-token-fixture";
        const string smtp = "private-smtp-credential-fixture";
        using var diagnostics = new DevelopmentDiagnostics(State, [token, smtp]);
        var logger = diagnostics.CreateLogger("DeckCapture.Tests");
        logger.LogError(new IOException($"authorization=Bearer {token}; Password=unguessable-password; smtp {smtp}; https://user:uri-password@fixture.invalid/path?signature=query-secret"),
            "Credential failure {Detail}", "{\"password\":\"json-password\",\"api_key\":\"api-secret\"}");
        foreach (var secret in new[] { token, smtp, "unguessable-password", "uri-password", "query-secret", "json-password", "api-secret" })
        {
            var raw = File.ReadAllText(Path.Combine(State, "logs", "diagnostics-0.jsonl"));
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(diagnostics.ReadRecent()), StringComparison.Ordinal);
        }
        var download = await diagnostics.ReadLogAsync("log-0");
        var line = Encoding.UTF8.GetString(download.Content).Trim();
        Assert.NotNull(JsonSerializer.Deserialize<DiagnosticsEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Contains("redacted", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestSummaryOmitsQueryAndFrameworkRequestLogs()
    {
        using var diagnostics = new DevelopmentDiagnostics(State);
        diagnostics.RecordRequest("GET", "/api/state?token=do-not-retain", 200, 1);
        Assert.Empty(diagnostics.ReadRecent());
        diagnostics.SetEnabled(true);
        diagnostics.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("Raw query/body must not be retained");
        diagnostics.RecordRequest("POST", "/api/recordings/prepare?unlabelled=sensitive-query", 201, 12.34);
        var entries = diagnostics.ReadRecent();
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("Raw query", StringComparison.Ordinal));
        var summary = Assert.Single(entries, entry => entry.Category == "DeckCapture.HttpSummary");
        Assert.Contains("POST /api/recordings/prepare -> 201", summary.Message);
        Assert.DoesNotContain("sensitive-query", summary.Message);
        Assert.DoesNotContain("?", summary.Message);
        diagnostics.SetEnabled(false);
        diagnostics.RecordRequest("GET", "/api/state?token=never", 500, 2, new IOException("password=exception-secret"));
        Assert.Contains(diagnostics.ReadRecent(), entry => entry.Level == "Error" && entry.Category == "DeckCapture.HttpSummary");
        Assert.DoesNotContain("exception-secret", JsonSerializer.Serialize(diagnostics.ReadRecent()));
    }

    [Theory]
    [InlineData("../diagnostics-settings.json")]
    [InlineData("log-0/../../secrets")]
    [InlineData("log-99")]
    [InlineData("C:\\private.txt")]
    public async Task DownloadsAcceptOnlyFixedLogIdentities(string id)
    {
        using var diagnostics = new DevelopmentDiagnostics(State);
        await Assert.ThrowsAsync<CommandException>(() => diagnostics.ReadLogAsync(id));
    }

    [Fact]
    public async Task EvidenceReadsOnlyAllowlistedFilesAndBoundsTail()
    {
        using var diagnostics = new DevelopmentDiagnostics(State, ["worker-secret"], SmallLimits());
        var cache = Path.Combine(temp, "cache");
        var id = Guid.NewGuid().ToString("N");
        var spool = Path.Combine(cache, ".spool", id);
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "encoder.log"), string.Concat(Enumerable.Repeat("older evidence line\n", 200)) + "password=worker-secret\nnewest retained evidence\n");
        File.WriteAllText(Path.Combine(spool, "options.json"), "Private recording options must never be exposed");
        var files = diagnostics.GetEvidenceFiles(cache, id);
        Assert.Equal("encoder", Assert.Single(files).Id);
        var content = Encoding.UTF8.GetString((await diagnostics.ReadEvidenceAsync(cache, id, "encoder")).Content);
        Assert.Contains("newest retained evidence", content);
        Assert.DoesNotContain("worker-secret", content);
        Assert.True(Encoding.UTF8.GetByteCount(content) < 1200);
        await Assert.ThrowsAsync<CommandException>(() => diagnostics.ReadEvidenceAsync(cache, id, "options.json"));
        await Assert.ThrowsAsync<CommandException>(() => diagnostics.ReadEvidenceAsync(cache, "../escape", "encoder"));
    }

    [Fact]
    public async Task EvidenceAndLogReparseEscapesAreRefused()
    {
        using var diagnostics = new DevelopmentDiagnostics(State);
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        var privateFile = Path.Combine(outside, "private.log");
        File.WriteAllText(privateFile, "Private outside evidence");
        File.CreateSymbolicLink(Path.Combine(State, "logs", "diagnostics-0.jsonl"), privateFile);
        await Assert.ThrowsAsync<CommandException>(() => diagnostics.ReadLogAsync("log-0"));
        var cache = Path.Combine(temp, "cache");
        Directory.CreateDirectory(cache);
        Directory.CreateSymbolicLink(Path.Combine(cache, ".spool"), outside);
        await Assert.ThrowsAsync<CommandException>(() => diagnostics.ReadEvidenceAsync(cache, Guid.NewGuid().ToString("N"), "encoder"));
    }

    [Fact]
    public void UnsafeDiagnosticsStorageCannotCrashALoggingCaller()
    {
        using var diagnostics = new DevelopmentDiagnostics(State);
        var outside = Path.Combine(temp, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(Path.Combine(State, "logs"));
        Directory.CreateSymbolicLink(Path.Combine(State, "logs"), outside);
        diagnostics.CreateLogger("DeckCapture.Tests").LogError("Capture error remains available in the ring");
        Assert.Single(diagnostics.ReadRecent());
        Assert.NotNull(diagnostics.GetStatus().LastWriteError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task BundleContainsSanitizedRuntimeConfigurationAndKnownEvidenceOnly()
    {
        using var diagnostics = new DevelopmentDiagnostics(State, ["bundle-access-token", "bundle-smtp-password"]);
        diagnostics.CreateLogger("DeckCapture.Tests").LogInformation("Source initialized");
        var cache = Path.Combine(temp, "cache");
        var id = Guid.NewGuid().ToString("N");
        var spool = Path.Combine(cache, ".spool", id);
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "timing.json"), "{\"seconds\":1}");
        File.WriteAllText(Path.Combine(spool, "options.json"), "Excluded secret recording metadata");
        File.WriteAllText(Path.Combine(State, "smtp.secrets.json"), "Excluded protected secret file");
        var config = ConfigurationRules.DevelopmentDefaults(temp) with { Notifications = new() { Username = "private-user", CredentialReference = "private-reference",
            Sender = "private-sender@example.invalid", Recipients = ["private-recipient@example.invalid"] } };
        var bundle = await diagnostics.CreateBundleAsync(config, new(false, false, "fixture", "token=bundle-access-token; password=bundle-smtp-password"), [new(id, cache)]);
        using var zip = new ZipArchive(new MemoryStream(bundle.Content), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("runtime.json"));
        Assert.NotNull(zip.GetEntry("configuration.sanitized.json"));
        Assert.NotNull(zip.GetEntry($"capture/{id}/timing.txt"));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("options", StringComparison.Ordinal) || entry.FullName.Contains("smtp.secrets", StringComparison.Ordinal));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            foreach (var secret in new[] { "bundle-access-token", "bundle-smtp-password", "private-user", "private-reference", "private-sender", "private-recipient", "Excluded secret" })
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            if (entry.FullName.EndsWith(".json", StringComparison.Ordinal)) using (JsonDocument.Parse(text)) { }
        }
    }

    [Fact]
    public void OversizedUnstructuredInputIsOmittedWithoutLeakingAPartialSecret()
    {
        using var diagnostics = new DevelopmentDiagnostics(State);
        var text = diagnostics.Redact("password=" + new string('s', 300_000));
        Assert.Contains("omitted", text);
        Assert.DoesNotContain("ssss", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}
