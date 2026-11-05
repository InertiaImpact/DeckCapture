using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeckCapture.Capture;
using DeckCapture.Delivery;
using DeckCapture.Domain;
using DeckCapture.Supervisor;
using Microsoft.AspNetCore.DataProtection;

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot)) webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRoot });
var stateDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("DECKCAPTURE_STATE_DIRECTORY")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckCapture"));
Directory.CreateDirectory(stateDirectory);
// File-share exclusion protects against two supervisor processes using the same operational database.
using var instanceLock = new FileStream(Path.Combine(stateDirectory, "supervisor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5278";
builder.WebHost.UseUrls(urls);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2_000_000);
var accessToken = Environment.GetEnvironmentVariable("DECKCAPTURE_ACCESS_TOKEN");
bool LocalHost(string host) => DevelopmentAccessPolicy.IsLocalHost(host);
var developmentHttp = Environment.GetEnvironmentVariable("DECKCAPTURE_DEVELOPMENT_HTTP") == "1";
var remoteBinding = DevelopmentAccessPolicy.ValidateBinding(urls, accessToken, developmentHttp);
var diagnostics = new DevelopmentDiagnostics(stateDirectory, new[] { accessToken ?? "" });
builder.Services.AddSingleton(diagnostics);
builder.Services.AddWindowsService(options => options.ServiceName = "DeckCapture");
// All runtime log sinks must pass the same redaction and retention policy.
builder.Logging.ClearProviders();
builder.Logging.AddProvider(diagnostics);
builder.Logging.AddFilter<DevelopmentDiagnostics>(null, LogLevel.Trace);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(3));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
var protection = builder.Services.AddDataProtection().SetApplicationName("DeckCapture").PersistKeysToFileSystem(new(Path.Combine(stateDirectory, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddSingleton(new StateStore(stateDirectory));
builder.Services.AddSingleton(service =>
{
    var secrets = new SecretStore(service.GetRequiredService<IDataProtectionProvider>(), stateDirectory, diagnostics);
    try { diagnostics.RegisterSecret(secrets.ReadSmtp()); } catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { diagnostics.CreateLogger("DeckCapture.Secrets").LogWarning("Stored SMTP credential is unavailable; capture remains available."); }
    return secrets;
});
builder.Services.AddSingleton<ICaptureBackend, CaptureBackend>();
builder.Services.AddSingleton<IFileDelivery, FileDeliveryEngine>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton(service => new CaptureSupervisor(service.GetRequiredService<StateStore>(), service.GetRequiredService<ICaptureBackend>(),
    service.GetRequiredService<IFileDelivery>(), service.GetRequiredService<NotificationService>(), service.GetRequiredService<ILogger<CaptureSupervisor>>(), stateDirectory));
builder.Services.AddHostedService(service => service.GetRequiredService<CaptureSupervisor>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    Exception? failure = null;
    try { await next(); }
    catch (Exception error) { failure = error; throw; }
    finally { diagnostics.RecordRequest(context.Request.Method, context.Request.Path, failure is null ? context.Response.StatusCode : 500, elapsed.Elapsed.TotalMilliseconds, failure); }
});
// Serialize retries of a key without making a slow input block every browser command.
var requestStripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob:; media-src 'self' blob:; worker-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    if (!remoteBinding && !LocalHost(context.Request.Host.Host)) { context.Response.StatusCode = 403; return; }
    var api = context.Request.Path.StartsWithSegments("/api");
    if (api) context.Response.Headers.CacheControl = "no-store";
    if (api && context.Request.Path != "/api/health" && !string.IsNullOrEmpty(accessToken))
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + accessToken;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
        { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { Error = "A configured access token is required." }); return; }
    }
    var mutation = api && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
    if (!mutation) { await next(); return; }
    if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; await context.Response.WriteAsJsonAsync(new { Error = "Use application/json." }); return; }
    if (context.Request.Headers.Origin.FirstOrDefault() is { } origin && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Authority != context.Request.Host.Value || uri.Scheme != context.Request.Scheme))
    { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { Error = "Cross-origin commands are not permitted." }); return; }
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 8 or > 128 || key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_')))
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { Error = "Supply an Idempotency-Key of 8–128 letters, digits, hyphens or underscores." }); return; }
    context.Request.EnableBuffering();
    using var bodyReader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
    var body = await bodyReader.ReadToEndAsync(); context.Request.Body.Position = 0;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    var path = context.Request.Method + " " + context.Request.Path;
    var store = context.RequestServices.GetRequiredService<StateStore>();
    var requests = requestStripes[SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] & 63];
    await requests.WaitAsync(context.RequestAborted);
    var originalBody = context.Response.Body;
    try
    {
        var previous = store.ReadRequest(key);
        if (previous is { } saved)
        {
            if (saved.Path != path || saved.Hash != hash) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { Error = "This idempotency key belongs to a different request." }); return; }
            context.Response.StatusCode = saved.Status; context.Response.ContentType = "application/json"; await context.Response.WriteAsync(saved.Response); return;
        }
        await using var output = new MemoryStream(); context.Response.Body = output;
        try { await next(); }
        catch (CommandException e)
        {
            output.SetLength(0); output.Position = 0;
            context.Response.StatusCode = e.StatusCode;
            await AcceptedRecordingJsonResult.WriteAsync(context, new { Error = e.Message });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            output.SetLength(0); output.Position = 0;
            context.Response.StatusCode = 400;
            await AcceptedRecordingJsonResult.WriteAsync(context, new { Error = e.Message });
        }
        output.Position = 0; using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true); var response = await reader.ReadToEndAsync();
        if (context.Response.StatusCode < 500) store.SaveRequest(key, path, hash, context.Response.StatusCode, response);
        context.Response.Body = originalBody; await context.Response.WriteAsync(response);
    }
    finally { context.Response.Body = originalBody; requests.Release(); }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapPost("/api/inputs/pcie-probe", (CaptureSupervisor supervisor, CancellationToken ct) => supervisor.ProbePcieIdentityAsync(ct));
app.MapGet("/api/inputs/catalog", () => Results.Json(new { models = DeckLinkModelCatalog.Models }));
app.MapGet("/api/inputs/discover", (CaptureSupervisor supervisor, CancellationToken ct) => supervisor.DiscoverInputsAsync(ct));
app.MapPost("/api/inputs/formats", (InputFormatsCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.DiscoverInputFormatsAsync(command.DeviceName, ct));
app.MapGet("/api/diagnostics/status", () => diagnostics.GetStatus());
app.MapPost("/api/diagnostics/settings", (ToggleCommand command) => diagnostics.SetEnabled(command.Enabled));
app.MapGet("/api/diagnostics/logs", () => Results.Ok(new { Entries = diagnostics.ReadRecent() }));
app.MapGet("/api/diagnostics/bundle", async (CaptureSupervisor supervisor, CancellationToken ct) =>
{
    var download = await supervisor.DiagnosticsBundleAsync(diagnostics, ct);
    return Results.File(download.Content, download.ContentType, download.FileName);
});
app.MapPost("/api/maintenance/drain", (ToggleCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.SetMaintenanceAsync(command.Enabled, ct));
app.MapGet("/api/health", () => Results.Ok(new { Status = "Ready", Version = "0.1.0-pilot" }));
app.MapGet("/api/state", (CaptureSupervisor supervisor, CancellationToken ct) => supervisor.SnapshotAsync(ct));
app.MapGet("/api/events", async (HttpContext context, CaptureSupervisor supervisor, IHostApplicationLifetime lifetime) =>
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
    var token = linked.Token;
    context.Response.ContentType = "text/event-stream"; context.Response.Headers.CacheControl = "no-store";
    try
    {
        while (!token.IsCancellationRequested)
        {
            var snapshot = await supervisor.SnapshotAsync(token);
            await context.Response.WriteAsync("event: state\ndata: " + JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(StateStore.Json) { WriteIndented = false }) + "\n\n", token);
            await context.Response.Body.FlushAsync(token); await Task.Delay(1000, token);
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
});
app.MapPost("/api/recordings/prepare", async (PrepareRecordingRequest request, CaptureSupervisor supervisor, CancellationToken ct) =>
    new AcceptedRecordingJsonResult(await supervisor.PrepareAsync(request, ct)));
app.MapPost("/api/recordings/{id}/start", async (string id, RevisionCommand command, CaptureSupervisor supervisor, CancellationToken ct) =>
    new AcceptedRecordingJsonResult(await supervisor.StartAsync(id, command.Revision, ct)));
app.MapPost("/api/recordings/{id}/stop", (string id, RevisionCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.StopAsync(id, command.Revision, ct));
app.MapPost("/api/recordings/{id}/cancel", (string id, RevisionCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.CancelAsync(id, command.Revision, ct));
app.MapPost("/api/recordings/{id}/extend", (string id, ExtendCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.ExtendAsync(id, command.Revision, command.Seconds, ct));
app.MapPost("/api/recordings/{id}/review", (string id, RevisionCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.ReviewAsync(id, command.Revision, ct));
app.MapGet("/api/channels/{id}/preview", async (string id, CaptureSupervisor supervisor) =>
{
    var path = await supervisor.PreviewPathAsync(id);
    if (path is null || !File.Exists(path)) return Results.NotFound();
    // Copy a snapshot while the worker can replace the next JPEG; do not serve an open growing file.
    try { return Results.File(await File.ReadAllBytesAsync(path), "image/jpeg"); } catch (IOException) { return Results.NotFound(); }
});
app.MapGet("/api/channels/{id}/live/{jobId}/{fileName}", async (string id, string jobId, string fileName, CaptureSupervisor supervisor, CancellationToken ct) =>
{
    var resource = await supervisor.ReadLivePreviewAsync(id, jobId, fileName, ct);
    return resource is null ? Results.NotFound() : Results.File(resource.Content, resource.ContentType);
});
app.MapPost("/api/channels/{id}/monitor/start", (string id, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.StartMonitorAsync(id, ct));
app.MapPost("/api/channels/{id}/monitor/stop", (string id, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.StopMonitorAsync(id, ct));
app.MapPost("/api/deliveries/{id}/override", (string id, ToggleCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.DeliveryCommandAsync(id, "override", command.Enabled, ct));
app.MapPost("/api/deliveries/{id}/hold", (string id, ToggleCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.DeliveryCommandAsync(id, "hold", command.Enabled, ct));
app.MapPost("/api/deliveries/{id}/retry", (string id, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.DeliveryCommandAsync(id, "retry", true, ct));
app.MapPost("/api/transfers/override", (OverrideCommand command, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.SetTransferOverrideAsync(command.Enabled, command.Hours, ct));
app.MapGet("/api/configuration", (CaptureSupervisor supervisor) => supervisor.ConfigurationAsync());
app.MapPost("/api/configuration/validate", (AppConfiguration candidate) => { var errors = ConfigurationRules.Validate(candidate); return Results.Ok(new { Valid = errors.Count == 0, Errors = errors }); });
app.MapPut("/api/configuration", (AppConfiguration candidate, CaptureSupervisor supervisor, CancellationToken ct) => supervisor.UpdateConfigurationAsync(candidate, ct));
app.MapGet("/api/destinations/{id}/folders", async (string id, string? relative, CaptureSupervisor supervisor) =>
{
    try
    {
        var configuration = await supervisor.ConfigurationAsync(); var destination = configuration.Destinations.FirstOrDefault(d => d.Id == id);
        if (destination is null) return Results.NotFound();
        var path = PathSafety.UnderRoot(destination.Root, relative ?? "");
        var folders = Directory.Exists(path) ? Directory.EnumerateDirectories(path).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).Take(500).Select(p => new { Name = Path.GetFileName(p), Relative = Path.GetRelativePath(destination.Root, p) }).ToArray() : [];
        return Results.Ok(new { Relative = relative ?? "", Folders = folders });
    }
    catch (Exception e) when (e is CommandException or IOException or UnauthorizedAccessException or ArgumentException) { return Results.BadRequest(new { Error = e.Message }); }
});
app.MapGet("/api/recordings/{id}/qc", async (string id, CaptureSupervisor supervisor) =>
{
    try { return Results.Ok(await supervisor.QcAsync(id)); } catch (CommandException e) { return Results.Json(new { Error = e.Message }, statusCode: e.StatusCode); }
});
app.MapPost("/api/reports/preview", async (ReportCommand command, CaptureSupervisor supervisor) => { var report = await supervisor.ReportAsync(command.Kind); return Results.Ok(new { report.Subject, report.Body }); });
app.MapGet("/api/secrets/smtp", (SecretStore secrets) => Results.Ok(secrets.SmtpStatus()));
app.MapPost("/api/secrets/smtp", (SecretCommand command, SecretStore secrets) =>
{
    var status = secrets.ChangeSmtp(command.Action, command.Value);
    return Results.Ok(new { Saved = true, status.SecretConfigured, status.Status });
});
app.MapPost("/api/notifications/test", async (CaptureSupervisor supervisor, NotificationService notifications) =>
{
    var config = await supervisor.ConfigurationAsync(); if (!config.Notifications.Enabled) throw new CommandException(409, "Enable and configure SMTP before requesting a test message.");
    notifications.Queue("DeckCapture SMTP test", "This test was explicitly requested from the DeckCapture interface."); return Results.Ok(new { Queued = true });
});
await app.RunAsync();

public sealed record RevisionCommand(long Revision);
public sealed record ExtendCommand(long Revision, int Seconds);
public sealed record ToggleCommand(bool Enabled);
public sealed record OverrideCommand(bool Enabled, int Hours = 12);
public sealed record ReportCommand(string Kind = "Current summary");
public sealed record SecretCommand(string? Value = null, string? Action = null);
public partial class Program;

public sealed record InputFormatsCommand(string DeviceName);
