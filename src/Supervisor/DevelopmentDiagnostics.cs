using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed record DiagnosticsLimits(int FileBytes = 1_048_576, int FileCount = 4, int MemoryEntries = 500,
    int EntryCharacters = 8192, int EvidenceBytes = 131_072, int BundleJobs = 10);
public sealed record DiagnosticsStatus(bool Enabled, string MinimumLevel, int FileBytes, int FileCount,
    int MemoryEntries, string? LastWriteError);
public sealed record DiagnosticsEntry(DateTimeOffset Timestamp, string Level, string Category, int EventId, string Message, string? Exception);
public sealed record DiagnosticsLogFile(string Id, long Bytes, DateTimeOffset LastWriteUtc);
public sealed record DiagnosticsEvidenceFile(string Id, long Bytes, DateTimeOffset LastWriteUtc);
public sealed record DiagnosticsEvidenceSource(string JobId, string CacheRoot);
public sealed record DiagnosticsDownload(string FileName, string ContentType, byte[] Content);

/// <summary>Bounded local diagnostics. Remote authorization belongs to the API;
/// this service accepts fixed file identities, never a caller-selected file path.</summary>
public sealed class DevelopmentDiagnostics : ILoggerProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> EvidenceNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["encoder"] = "encoder.log", ["timing"] = "timing.json", ["validation"] = "validation.json",
        ["worker-stdout"] = "worker-stdout.log", ["worker-stderr"] = "worker-stderr.log"
    };
    private static readonly Regex Authorization = Pattern(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=\-]+");
    private static readonly Regex UserInfo = Pattern(@"(?<=://)[^/\s@]+@");
    private static readonly Regex UrlQuery = Pattern(@"(?<url>https?://[^\s?]+)\?[^\s]+");
    private static readonly Regex KeyValue = Pattern("(?<key>[\"']?(?:authorization|password|passwd|pwd|secret|(?:access[_-]?)?token|api[_-]?key|credential(?:Reference)?|user(?:name|[_ ]?id)|signature|sig)[\"']?\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;}\\]]+)");
    private static readonly Regex ArgumentSecret = Pattern("(?<key>--?(?:password|token|secret|api-key|credential)\\s+)(?:\"[^\"]*\"|'[^']*'|\\S+)");
    private static readonly Regex SensitiveKey = Pattern(@"password|passwd|secret|token|credential|authorization|username");
    private readonly object gate = new();
    private readonly string stateDirectory;
    private readonly string logsDirectory;
    private readonly DiagnosticsLimits limits;
    private readonly Queue<DiagnosticsEntry> recent = new();
    private readonly List<string> secrets = [];
    private bool enabled;
    private bool disposed;
    private string? lastWriteError;
    private sealed record Settings(bool Enabled);

    public DevelopmentDiagnostics(string stateDirectory, IEnumerable<string>? secrets = null, DiagnosticsLimits? limits = null)
    {
        this.limits = limits ?? new();
        if (this.limits.FileBytes is < 4096 or > 16_777_216 || this.limits.FileCount is < 1 or > 10 ||
            this.limits.MemoryEntries is < 1 or > 2000 || this.limits.EntryCharacters is < 256 or > 16_384 ||
            this.limits.EvidenceBytes is < 1024 or > 1_048_576 || this.limits.BundleJobs is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(limits), "Diagnostics limits exceed their bounded operating range.");
        if ((long)this.limits.FileBytes * this.limits.FileCount + (long)this.limits.EvidenceBytes * EvidenceNames.Count * this.limits.BundleJobs > 33_554_432)
            throw new ArgumentOutOfRangeException(nameof(limits), "Combined diagnostics retention and bundle evidence budget exceeds 32 MiB.");
        this.stateDirectory = PathSafety.UnderRoot(Path.GetFullPath(stateDirectory), "");
        Directory.CreateDirectory(this.stateDirectory);
        logsDirectory = PathSafety.UnderRoot(this.stateDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        _ = PathSafety.UnderRoot(this.stateDirectory, "logs");
        foreach (var secret in secrets ?? []) RegisterSecret(secret);
        var settings = StatePath("diagnostics-settings.json");
        if (File.Exists(settings))
        {
            try
            {
                if (new FileInfo(settings).Length > 4096) throw new InvalidDataException("Diagnostics settings exceed their size limit.");
                enabled = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settings), Json)?.Enabled ?? false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                enabled = false;
                lastWriteError = "Diagnostics settings could not be read; verbose logging remains disabled.";
            }
        }
        LoadRecent();
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private string StatePath(string name) => PathSafety.UnderRoot(stateDirectory, name);
    private string LogPath(int number) => PathSafety.UnderRoot(logsDirectory, $"diagnostics-{number}.jsonl");

    public DiagnosticsStatus GetStatus()
    {
        lock (gate) return new(enabled, enabled ? "Trace" : "Information", limits.FileBytes, limits.FileCount,
            limits.MemoryEntries, lastWriteError is null ? null : Redact(lastWriteError));
    }

    public DiagnosticsStatus SetEnabled(bool value)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = StatePath("diagnostics-settings.json");
            var temporary = StatePath("diagnostics-settings.new");
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, new Settings(value), Json);
                output.Flush(true);
            }
            _ = StatePath("diagnostics-settings.json");
            File.Move(temporary, target, true);
            enabled = value;
            Write(LogLevel.Information, "DeckCapture.Diagnostics", 0, value ? "Verbose diagnostics enabled." : "Verbose diagnostics disabled.", null);
            return GetStatus();
        }
    }

    // Register current access-token and SMTP credentials before their first use.
    // The small bounded history also redacts prior credentials during rotation.
    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        if (secret.Length > 2_000_000) throw new ArgumentOutOfRangeException(nameof(secret));
        lock (gate)
        {
            if (secrets.Contains(secret, StringComparer.Ordinal)) return;
            secrets.Add(secret);
            if (secrets.Count > 16) secrets.RemoveAt(0);
        }
    }

    public string Redact(string? text)
    {
        if (text is null) return "";
        // Fail closed rather than retaining a clipped fragment of a huge secret.
        if (text.Length > 262_144) return "[diagnostic text omitted: size limit]";
        string[] known;
        lock (gate) known = secrets.ToArray();
        foreach (var secret in known.OrderByDescending(value => value.Length)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        try
        {
            text = UserInfo.Replace(text, "[redacted]@");
            text = UrlQuery.Replace(text, match => match.Groups["url"].Value + "?[redacted]");
            text = Authorization.Replace(text, "[redacted authorization]");
            text = KeyValue.Replace(text, match => match.Groups["key"].Value + "\"[redacted]\"");
            return ArgumentSecret.Replace(text, match => match.Groups["key"].Value + "[redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic text omitted: redaction limit]"; }
    }

    private string Bounded(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + " [truncated]";
    private DiagnosticsEntry Sanitize(DiagnosticsEntry entry) => entry with
    {
        Level = Enum.TryParse<LogLevel>(entry.Level, out var level) && Enum.IsDefined(level) ? level.ToString() : "Information",
        Category = Bounded(Redact(entry.Category), 128), Message = Bounded(Redact(entry.Message), limits.EntryCharacters / 2),
        Exception = entry.Exception is null ? null : Bounded(Redact(entry.Exception), limits.EntryCharacters / 2)
    };

    private bool IsEnabled(string category, LogLevel level)
    {
        if (level == LogLevel.None) return false;
        // Framework request logs may include query strings. Only the dedicated
        // summary below records requests; HTTP bodies/headers/scopes are excluded.
        if (category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && level < LogLevel.Warning) return false;
        lock (gate) return !disposed && (level >= LogLevel.Information || enabled);
    }

    private void Write(LogLevel level, string category, int eventId, string message, Exception? error)
    {
        lock (gate)
        {
            if (!IsEnabled(category, level)) return;
            var entry = Sanitize(new(DateTimeOffset.UtcNow, level.ToString(), category, eventId, message, error?.ToString()));
            recent.Enqueue(entry);
            while (recent.Count > limits.MemoryEntries) recent.Dequeue();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
                while (bytes.Length > limits.FileBytes)
                {
                    entry = entry with { Message = Bounded(entry.Message, Math.Max(32, entry.Message.Length / 2)),
                        Exception = entry.Exception is null ? null : Bounded(entry.Exception, Math.Max(32, entry.Exception.Length / 2)) };
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
                }
                var active = LogPath(0);
                if (File.Exists(active) && new FileInfo(active).Length + bytes.Length > limits.FileBytes) Rotate();
                using var file = new FileStream(LogPath(0), FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
                file.Flush();
                lastWriteError = null;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CommandException)
            {
                // A broken diagnostics disk must never fail capture or delivery.
                lastWriteError = Redact(failure.GetType().Name + ": " + failure.Message);
            }
        }
    }

    private void Rotate()
    {
        var oldest = LogPath(limits.FileCount - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = limits.FileCount - 2; index >= 0; index--)
        {
            var source = LogPath(index);
            var target = LogPath(index + 1);
            if (File.Exists(source)) File.Move(source, target, true);
        }
    }

    private void LoadRecent()
    {
        for (var index = limits.FileCount - 1; index >= 0; index--)
        {
            try
            {
                var path = LogPath(index);
                if (!File.Exists(path)) continue;
                var content = ReadTailAsync(path, limits.FileBytes, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var line in content.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DiagnosticsEntry>(line, Json);
                        if (entry is not null) { recent.Enqueue(Sanitize(entry)); while (recent.Count > limits.MemoryEntries) recent.Dequeue(); }
                    }
                    catch (JsonException) { /* A torn final record is ignored; earlier complete records remain readable. */ }
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CommandException)
            { lastWriteError = "A retained diagnostic log was unavailable or unsafe to read."; }
        }
    }

    public IReadOnlyList<DiagnosticsEntry> ReadRecent(int maximum = 200)
    {
        lock (gate) return recent.TakeLast(Math.Clamp(maximum, 1, limits.MemoryEntries)).Reverse().Select(Sanitize).ToArray();
    }

    public IReadOnlyList<DiagnosticsLogFile> GetLogFiles()
    {
        lock (gate)
        {
            var result = new List<DiagnosticsLogFile>();
            for (var index = 0; index < limits.FileCount; index++)
            {
                var path = LogPath(index);
                if (File.Exists(path)) { var file = new FileInfo(path); result.Add(new($"log-{index}", file.Length, file.LastWriteTimeUtc)); }
            }
            return result;
        }
    }

    public Task<DiagnosticsDownload> ReadLogAsync(string id, CancellationToken cancellationToken = default)
    {
        var index = ParseLogId(id);
        string content;
        // Snapshot a bounded file under the same gate as rotation. Reads do not
        // hold a file handle across an await that could block a logger writer.
        lock (gate) content = ReadTailAsync(LogPath(index), limits.FileBytes, cancellationToken).GetAwaiter().GetResult();
        var sanitized = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            if (line.Length == 0) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<DiagnosticsEntry>(line, Json);
                if (entry is not null) sanitized.AppendLine(JsonSerializer.Serialize(Sanitize(entry), Json));
            }
            catch (JsonException) { /* Exclude torn or unrecognized raw records from downloads. */ }
        }
        return Task.FromResult(new DiagnosticsDownload($"deckcapture-{id}.jsonl", "application/x-ndjson", Encoding.UTF8.GetBytes(sanitized.ToString())));
    }

    private int ParseLogId(string id)
    {
        for (var index = 0; index < limits.FileCount; index++) if (id == $"log-{index}") return index;
        throw new CommandException(400, "Unknown diagnostic log ID.");
    }

    private static string EvidenceDirectory(string cacheRoot, string jobId)
    {
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new CommandException(400, "Invalid recording ID.");
        return PathSafety.UnderRoot(cacheRoot, Path.Combine(".spool", jobId));
    }

    public IReadOnlyList<DiagnosticsEvidenceFile> GetEvidenceFiles(string cacheRoot, string jobId)
    {
        var directory = EvidenceDirectory(cacheRoot, jobId);
        var result = new List<DiagnosticsEvidenceFile>();
        foreach (var pair in EvidenceNames)
        {
            var path = PathSafety.UnderRoot(directory, pair.Value);
            if (File.Exists(path)) { var file = new FileInfo(path); result.Add(new(pair.Key, file.Length, file.LastWriteTimeUtc)); }
        }
        return result;
    }

    public async Task<DiagnosticsDownload> ReadEvidenceAsync(string cacheRoot, string jobId, string evidenceId,
        CancellationToken cancellationToken = default)
    {
        if (!EvidenceNames.TryGetValue(evidenceId, out var name)) throw new CommandException(400, "Unknown capture evidence ID.");
        var path = PathSafety.UnderRoot(EvidenceDirectory(cacheRoot, jobId), name);
        var content = await ReadTailAsync(path, limits.EvidenceBytes, cancellationToken);
        return new($"capture-{jobId}-{evidenceId}.txt", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Redact(content)));
    }

    private static async Task<string> ReadTailAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var offset = Math.Max(0, length - maximumBytes);
        stream.Position = offset;
        var data = new byte[(int)Math.Min(length, maximumBytes)];
        var read = 0;
        while (read < data.Length)
        {
            var count = await stream.ReadAsync(data.AsMemory(read), cancellationToken);
            if (count == 0) break;
            read += count;
        }
        var text = Encoding.UTF8.GetString(data, 0, read);
        // Never expose a clipped secret/value from the first partial line.
        if (offset > 0) { var newline = text.IndexOf('\n'); text = newline < 0 ? "" : text[(newline + 1)..]; }
        return text;
    }

    public async Task<DiagnosticsDownload> CreateBundleAsync(AppConfiguration configuration, CaptureCapabilities? capabilities,
        IReadOnlyList<DiagnosticsEvidenceSource>? evidence = null, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var runtime = JsonSerializer.SerializeToNode(new
            {
                CreatedAt = DateTimeOffset.UtcNow, Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, Version = typeof(DevelopmentDiagnostics).Assembly.GetName().Version?.ToString(),
                Diagnostics = GetStatus(), Capabilities = capabilities
            }, Json);
            SanitizeConfiguration(runtime);
            AddText(zip, "runtime.json", runtime?.ToJsonString(Json) ?? "{}");
            var config = JsonSerializer.SerializeToNode(configuration, Json);
            SanitizeConfiguration(config);
            AddText(zip, "configuration.sanitized.json", config?.ToJsonString(Json) ?? "{}");
            foreach (var file in GetLogFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { var download = await ReadLogAsync(file.Id, cancellationToken); AddText(zip, $"logs/{file.Id}.jsonl", Encoding.UTF8.GetString(download.Content)); }
                catch (FileNotFoundException) { /* Rotation can remove an old ID between listing and snapshot. */ }
            }
            foreach (var source in (evidence ?? []).Distinct().Take(limits.BundleJobs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var file in GetEvidenceFiles(source.CacheRoot, source.JobId))
                    {
                        var download = await ReadEvidenceAsync(source.CacheRoot, source.JobId, file.Id, cancellationToken);
                        AddText(zip, $"capture/{source.JobId}/{file.Id}.txt", Encoding.UTF8.GetString(download.Content));
                    }
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CommandException)
                { AddText(zip, $"capture-unavailable-{Guid.NewGuid():N}.txt", "Capture evidence unavailable or refused by path confinement."); }
            }
        }
        return new($"deckcapture-diagnostics-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.zip", "application/zip", output.ToArray());
    }

    private void SanitizeConfiguration(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (SensitiveKey.IsMatch(pair.Key) || pair.Key is "sender" or "recipients") obj[pair.Key] = "[redacted]";
                else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) obj[pair.Key] = Redact(text);
                else SanitizeConfiguration(pair.Value);
            }
        }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = Redact(text);
                else SanitizeConfiguration(array[index]);
    }

    private void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        // All callers sanitize values before serialization, preserving valid
        // JSON/JSONL rather than applying regex replacements to encoded JSON.
        writer.Write(text);
    }

    public void RecordRequest(string method, string path, int status, double elapsedMilliseconds, Exception? error = null)
    {
        bool verbose;
        lock (gate) verbose = enabled;
        if (!verbose && status < 500 && error is null) return;
        var safePath = path.Split('?', '#')[0];
        var message = $"{Bounded(method, 12)} {Bounded(safePath, 512)} -> {status.ToString(CultureInfo.InvariantCulture)} in {Math.Max(0, elapsedMilliseconds).ToString("F1", CultureInfo.InvariantCulture)} ms";
        Write(status >= 500 || error is not null ? LogLevel.Error : LogLevel.Information, "DeckCapture.HttpSummary", 0, message, error);
    }

    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(this, categoryName);
    private sealed class DiagnosticLogger(DevelopmentDiagnostics owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(category, logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch { message = "Diagnostic formatter failed; event content omitted."; }
            owner.Write(logLevel, category, eventId.Id, message, exception);
        }
    }

    public void Dispose() { lock (gate) disposed = true; }
}
