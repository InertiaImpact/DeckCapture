using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Delivery;

namespace DeckCapture.Supervisor;

/// <summary>Authoritative job coordinator. Device/media I/O happens outside the state gate wherever it can block.</summary>
public sealed partial class CaptureSupervisor : BackgroundService
{
    private readonly StateStore _store;
    private readonly ICaptureBackend _capture;
    private readonly IFileDelivery _delivery;
    private readonly NotificationService _notifications;
    private readonly ILogger<CaptureSupervisor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RecordingJob> _jobs = [];
    private readonly Dictionary<string, DeliveryJob> _deliveries = [];
    private readonly Dictionary<string, LiveCapture> _live = [];
    private readonly Dictionary<string, OpeningCapture> _opening = [];
    private sealed class OpeningCapture(string channelId, CancellationToken lifetime)
    {
        public string ChannelId { get; } = channelId;
        public CancellationTokenSource Cancel { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, (Task Task, CancellationTokenSource Cancel)> _transfers = [];
    private readonly List<OperationalAlert> _alerts;
    private readonly List<Task> _finalizers = [];
    private readonly List<Task> _controlTasks = [];
    private Task _notificationTask = Task.CompletedTask;
    private AppConfiguration _configuration;
    private CaptureCapabilities _capabilities = new(false, false, "Probing");
    private DateTimeOffset? _overrideUntil;
    private bool _lastWindowOpen;
    private string? _lastDailyDate;
    private HashSet<string>? _recordingGroup;
    private bool _shuttingDown;
    private readonly CancellationTokenSource _lifetime = new();
    public string StateDirectory { get; }
    private sealed class LiveCapture(ICaptureSession session)
    {
        public ICaptureSession Session { get; } = session;
        public long StartTimestamp { get; set; }
        public long? BlackSince { get; set; }
        public long? GraceSince { get; set; }
        public long? ContentSince { get; set; }
        public bool SawNonContent { get; set; }
        public bool WarnedEnd { get; set; }
        public bool WarnedLoss { get; set; }
        public bool CommandPending { get; set; }
    }

    public CaptureSupervisor(StateStore store, ICaptureBackend capture, IFileDelivery delivery, NotificationService notifications,
        ILogger<CaptureSupervisor> logger, string stateDirectory)
    {
        _store = store; _capture = capture; _delivery = delivery; _notifications = notifications; _logger = logger; StateDirectory = stateDirectory;
        _configuration = store.ReadAll<AppConfiguration>("configuration").FirstOrDefault() ?? ConfigurationRules.DevelopmentDefaults(stateDirectory);
        var errors = ConfigurationRules.Validate(_configuration);
        if (errors.Count > 0) throw new InvalidDataException("Saved configuration is invalid: " + string.Join(" ", errors));
        Directory.CreateDirectory(PathSafety.UnderRoot(_configuration.CacheRoot, ""));
        _store.Save("configuration", "current", _configuration);
        foreach (var job in store.ReadAll<RecordingJob>("recording"))
        {
            if (IsActive(job.State))
            {
                job.State = JobState.Interrupted; job.Error = "Supervisor restarted. Retained source/spool requires operator review; recording was not resumed.";
                job.ReservedBytes = 0; job.Revision++; store.Save("recording", job.Id, job);
            }
            _jobs.Add(job.Id, job);
        }
        foreach (var job in store.ReadAll<DeliveryJob>("delivery"))
        {
            if (job.State is DeliveryState.Copying or DeliveryState.Verifying) { job.State = DeliveryState.RetryScheduled; job.NextAttemptAt = DateTimeOffset.UtcNow; store.Save("delivery", job.Id, job); }
            _deliveries.Add(job.Id, job);
        }
        _alerts = store.ReadAll<OperationalAlert>("alert").TakeLast(100).ToList();
        _overrideUntil = store.ReadAll<OverrideState>("override").FirstOrDefault()?.Until;
        _lastDailyDate = store.ReadAll<DailyReportState>("daily-report").FirstOrDefault()?.Date;
        var group = store.ReadAll<CaptureGroupState>("capture-group").FirstOrDefault()?.Ids.Where(_jobs.ContainsKey).ToHashSet();
        _recordingGroup = group?.Count > 0 ? group : null;
        var window = store.ReadAll<WindowState>("window-state").FirstOrDefault();
        _lastWindowOpen = window?.WasOpen ?? IsWindowOpen(); _windowSession = window?.Session; _windowOpenedAt = window?.OpenedAt;
    }
    public static bool IsActive(JobState state) => state is JobState.Prepared or JobState.Waiting or JobState.Recording or JobState.Finalizing or JobState.Validating;
    // Validation remains durable active work, but owns files rather than the input.
    // Tracked sessions/openings remain exclusive until their cleanup has completed.
    private bool OwnsInputUnderGate(RecordingJob job) => job.State is JobState.Prepared or JobState.Waiting or JobState.Recording or JobState.Finalizing ||
        _opening.ContainsKey(job.Id) || _live.ContainsKey(job.Id);
    internal Func<string, CaptureProfile, CancellationToken, Action<OfflineQcProgress>, Task<object?>>? OfflineQcAnalyzer { get; set; }
    private bool IsWindowOpen() => TransferWindowPolicy.IsWindowOpen(_configuration.Transfer, _configuration.TimeZone, DateTimeOffset.UtcNow);
    private bool OverrideActive => _overrideUntil > DateTimeOffset.UtcNow;
    private void Save(RecordingJob job) => _store.Save("recording", job.Id, job);
    private void Save(DeliveryJob job) => _store.Save("delivery", job.Id, job);
    private sealed record OverrideState(DateTimeOffset? Until);
    private sealed record DailyReportState(string Date);
    private sealed record CaptureGroupState(string[] Ids);
    public async Task<object> SnapshotAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            return new
            {
                Configuration = Clone(_configuration),
                Channels = _configuration.Channels.Select(channel => new
                {
                    Configuration = channel,
                    Job = Clone(_jobs.Values.Where(j => j.ChannelId == channel.Id).OrderByDescending(j => j.PreparedAt).FirstOrDefault()),
                    Monitoring = MonitoringUnderGate(channel.Id),
                    Metrics = PublicMetricsUnderGate(channel.Id)
                }).ToArray(),
                Recordings = Clone(_jobs.Values.OrderByDescending(j => j.PreparedAt).Take(200).ToArray()),
                Deliveries = Clone(_deliveries.Values.OrderByDescending(j => j.CreatedAt).Take(200).ToArray()),
                Alerts = _alerts.TakeLast(100).Reverse().ToArray(), Notifications = _notifications.Snapshot(),
                System = new { ProcessId = Environment.ProcessId, DevelopmentHttp = Environment.GetEnvironmentVariable("DECKCAPTURE_DEVELOPMENT_HTTP") == "1", StateDirectory, CacheFreeBytes = FreeSpace(_configuration.CacheRoot), ReservedBytes = _jobs.Values.Sum(j => j.ReservedBytes) + MonitorReservedBytes, WindowOpen = IsWindowOpen(), OverrideUntil = _overrideUntil, CaptureCapabilities = _capabilities, Version = "0.1.0-pilot" }
            };
        }
        finally { _gate.Release(); }
    }
    public async Task<AppConfiguration> ConfigurationAsync()
    {
        await _gate.WaitAsync(); try { return Clone(_configuration); } finally { _gate.Release(); }
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, StateStore.Json), StateStore.Json)!;
    public static long FreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    public async Task<RecordingJob> PrepareAsync(PrepareRecordingRequest request, CancellationToken token)
    {
        if (request.StartImmediately && request.WaitForContent) throw new CommandException(400, "Choose immediate recording or waiting for content, not both.");
        var prepared = await PrepareCoreAsync(request, token);
        // Acceptance owns the remaining operation. A disconnected browser cannot strand
        // the one-click command in Prepared by canceling its separate start gate wait.
        return request.StartImmediately ? await StartAsync(prepared.Id, prepared.Revision, CancellationToken.None) : prepared;
    }

    private async Task<RecordingJob> PrepareCoreAsync(PrepareRecordingRequest request, CancellationToken token)
    {
        var preparation = Stopwatch.StartNew();
        RecordingJob job; ChannelConfiguration channel; OpeningCapture opening; IdleMonitor? retiringMonitor;
        await _gate.WaitAsync(token);
        try
        {
            if (_shuttingDown || MaintenanceActive) throw new CommandException(409, "Supervisor is shutting down or reserved for maintenance.");
            EnsureInputDiscoveryIdleUnderGate();
            PathSafety.ValidateFileName(request.FileName);
            if (request.DurationSeconds is < 1 or > 43200) throw new CommandException(400, "Duration must be between 1 second and 12 hours.");
            if (request.EndMode is not ("Off" or "Alert" or "Stop")) throw new CommandException(400, "End mode must be Off, Alert or Stop.");
            channel = _configuration.Channels.FirstOrDefault(c => c.Id == request.ChannelId) ?? throw new CommandException(404, "Channel not found.");
            EnsureInputUnownedUnderGate(channel, allowOwnMonitor: true);
            if (_jobs.Values.Any(j => j.ChannelId == channel.Id && OwnsInputUnderGate(j))) throw new CommandException(409, "Channel already has a prepared, recording, or finalizing job.");
            var destination = _configuration.Destinations.FirstOrDefault(d => d.Id == request.DestinationId) ?? throw new CommandException(400, "Choose an approved destination.");
            _ = PathSafety.UnderRoot(destination.Root, request.RelativeDirectory ?? "");
            if (_jobs.Values.Any(j => j.FileName.Equals(request.FileName, StringComparison.OrdinalIgnoreCase) && j.DestinationRoot == destination.Root && j.State is not (JobState.Canceled or JobState.Failed or JobState.Interrupted))) throw new CommandException(409, "This filename already belongs to an existing job. Choose a unique filename.");
            var metadata = new Dictionary<string, string>();
            foreach (var field in _configuration.MetadataFields)
            {
                if (!field.Value.Visible) continue;
                var value = request.Metadata?.GetValueOrDefault(field.Key)?.Trim();
                if (field.Value.Required && string.IsNullOrEmpty(value)) throw new CommandException(400, $"{field.Key} is required.");
                if (value?.Length > 2000) throw new CommandException(400, "Metadata values must be at most 2000 characters.");
                if (!string.IsNullOrEmpty(value)) metadata.Add(field.Key, value);
            }
            // Continuous spool and final output coexist until delivery/retention review. Reserve both, not only the final file.
            var reserve = checked(_configuration.Capture.ReservationBytesPerSecond * (request.DurationSeconds + _configuration.Capture.PreRollSeconds + 3L) * 2);
            var replacingMonitorReserve = _monitors.TryGetValue(channel.Id, out var ownMonitor) ? ownMonitor.ReservedBytes : 0;
            if (FreeSpace(_configuration.CacheRoot) - _jobs.Values.Sum(j => j.ReservedBytes) - (MonitorReservedBytes - replacingMonitorReserve) - reserve < _configuration.EmergencyReserveBytes) throw new CommandException(409, "Insufficient unreserved cache space for the requested duration.");
            var id = Guid.NewGuid().ToString("N");
            var package = PathSafety.UnderRoot(_configuration.CacheRoot, Path.Combine("jobs", id));
            Directory.CreateDirectory(package);
            _ = PathSafety.UnderRoot(_configuration.CacheRoot, Path.Combine("jobs", id));
            job = new RecordingJob
            {
                Id = id, ChannelId = channel.Id, FileName = request.FileName, DurationSeconds = request.DurationSeconds, OriginalDurationSeconds = request.DurationSeconds,
                DestinationId = destination.Id, DestinationRoot = destination.Root, RelativeDirectory = request.RelativeDirectory ?? "", PackageDirectory = package,
                Metadata = metadata, Configuration = Clone(_configuration), Channel = channel, ReservedBytes = reserve,
                State = request.WaitForContent ? JobState.Waiting : JobState.Prepared, EndMode = _configuration.ExperimentalEndDetection ? request.EndMode : "Off",
                ExperimentalEndDetectionActive = _configuration.ExperimentalEndDetection, BatchId = request.BatchId
            };
            _jobs.Add(id, job); Save(job);
            opening = new OpeningCapture(channel.Id, _lifetime.Token); _opening.Add(id, opening);
            _monitors.TryGetValue(channel.Id, out retiringMonitor);
            if (retiringMonitor is not null) { retiringMonitor.Closing = true; retiringMonitor.CancelInput(); }
            _recordingGroup ??= [];
            _recordingGroup.Add(id);
            _store.SaveBatch([("recording", job.Id, JsonSerializer.Serialize(job, StateStore.Json)),
                ("capture-group", "current", JsonSerializer.Serialize(new CaptureGroupState(_recordingGroup.ToArray()), StateStore.Json))]);
        }
        finally { _gate.Release(); }
        ICaptureSession? opened = null;
        try
        {
            _logger.LogInformation("Capture {JobId} on {ChannelId}: accepted; opening input (immediate start: {StartImmediately})", job.Id, channel.Id, request.StartImmediately);
            if (retiringMonitor is not null) await CloseMonitorAsync(retiringMonitor);
            _logger.LogInformation("Capture {JobId}: prior monitor released after {ElapsedMs} ms", job.Id, preparation.ElapsedMilliseconds);
            // Opening one input may take seconds; other channels and control requests must keep running.
            var spool = PathSafety.UnderRoot(job.Configuration.CacheRoot, Path.Combine(".spool", job.Id));
            Directory.CreateDirectory(spool);
            _ = PathSafety.UnderRoot(job.Configuration.CacheRoot, Path.Combine(".spool", job.Id));
            opening.Cancel.CancelAfter(TimeSpan.FromSeconds(45));
            opened = await _capture.OpenAsync(new(channel, job.Configuration.Capture, spool), opening.Cancel.Token);
            _logger.LogInformation("Capture {JobId}: input ready after {ElapsedMs} ms", job.Id, preparation.ElapsedMilliseconds);
            await _gate.WaitAsync();
            try
            {
                if (_shuttingDown || job.State != JobState.Prepared && job.State != JobState.Waiting)
                    throw new CommandException(409, "Preparation was canceled or the supervisor is shutting down.");
                _live.Add(job.Id, new(opened) { SawNonContent = SignalObservationPolicy.HasFreshAnalysis(opened.Metrics) && !IsContent(opened.Metrics) });
                opened = null; return Clone(job);
            }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            if (opened is not null) await opened.DisposeAsync();
            await _gate.WaitAsync();
            try
            {
                if (job.State is JobState.Prepared or JobState.Waiting)
                {
                    job.State = JobState.Failed; job.Error = e.Message; job.ReservedBytes = 0; job.Revision++; Save(job);
                    Alert("Error", $"Could not open {channel.Label}: {e.Message}");
                }
            }
            finally { _gate.Release(); }
            throw new CommandException(409, "Capture input could not be opened: " + e.Message);
        }
        finally
        {
            await _gate.WaitAsync();
            try { _opening.Remove(job.Id); opening.Finished.TrySetResult(); opening.Cancel.Dispose(); }
            finally { _gate.Release(); }
        }
    }
    public async Task<RecordingJob> StartAsync(string id, long revision, CancellationToken token)
    {
        Task<RecordingJob> command;
        await _gate.WaitAsync(token);
        try { command = BeginUnderGate(FindJob(id, revision)); }
        finally { _gate.Release(); }
        return await command;
    }
    private Task<RecordingJob> BeginUnderGate(RecordingJob job)
    {
        if (_shuttingDown || MaintenanceActive) throw new CommandException(409, "Supervisor is shutting down or reserved for maintenance.");
        if (job.State is not (JobState.Prepared or JobState.Waiting)) throw new CommandException(409, "Only a prepared or waiting job can start.");
        if (!_live.TryGetValue(job.Id, out var live)) throw new CommandException(409, "Capture session is opening or unavailable. Wait or prepare again.");
        if (live.CommandPending) throw new CommandException(409, "A channel command is already in progress.");
        live.CommandPending = true;
        var preRoll = job.State == JobState.Waiting ? job.Configuration.Capture.PreRollSeconds : 0;
        var task = Task.Run(async () =>
        {
            try
            {
                var timestamp = Stopwatch.GetTimestamp(); var startedAt = DateTimeOffset.UtcNow;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await live.Session.BeginRecordingAsync(Path.Combine(job.PackageDirectory, job.FileName + ".mkv"), job.DurationSeconds, preRoll, timeout.Token);
                _logger.LogInformation("Capture {JobId}: recording start acknowledged after {ElapsedMs} ms", job.Id, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
                await _gate.WaitAsync();
                try
                {
                    live.CommandPending = false; live.StartTimestamp = timestamp;
                    job.StartedAt = startedAt; job.State = JobState.Recording; job.Revision++; Save(job);
                    if (_shuttingDown) BeginFinalizationUnderGate(job, "Supervisor shutdown");
                    return Clone(job);
                }
                finally { _gate.Release(); }
            }
            catch (Exception e)
            {
                await _gate.WaitAsync();
                try
                {
                    live.CommandPending = false; job.State = JobState.Failed; job.Error = "Start acknowledgement failed; retained media requires review: " + e.Message;
                    job.ReservedBytes = 0; job.Revision++; Save(job); _live.Remove(job.Id);
                    Alert("Error", $"{job.FileName}: {job.Error}");
                }
                finally { _gate.Release(); }
                await live.Session.DisposeAsync();
                throw new CommandException(409, job.Error);
            }
        });
        _controlTasks.Add(task); return task;
    }
    public async Task<RecordingJob> StopAsync(string id, long revision, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var job = FindJob(id, revision);
            if (job.State != JobState.Recording) throw new CommandException(409, "Job is not recording.");
            BeginFinalizationUnderGate(job, "Operator stopped"); return Clone(job);
        }
        finally { _gate.Release(); }
    }
    public async Task<RecordingJob> CancelAsync(string id, long revision, CancellationToken token)
    {
        ICaptureSession? session = null; RecordingJob result;
        await _gate.WaitAsync(token);
        try
        {
            var job = FindJob(id, revision);
            if (job.State is not (JobState.Prepared or JobState.Waiting)) throw new CommandException(409, "Only prepared or waiting jobs can be canceled.");
            if (_live.TryGetValue(id, out var pending) && pending.CommandPending) throw new CommandException(409, "A channel command is already in progress.");
            if (_opening.TryGetValue(id, out var opening)) opening.Cancel.Cancel();
            if (_live.Remove(id, out var live)) session = live.Session;
            job.State = JobState.Canceled; job.ReservedBytes = 0; job.Revision++; job.FinishedAt = DateTimeOffset.UtcNow; Save(job); result = Clone(job);
        }
        finally { _gate.Release(); }
        if (session is not null) await session.DisposeAsync(); return result;
    }
    public async Task<RecordingJob> ExtendAsync(string id, long revision, int seconds, CancellationToken token)
    {
        RecordingJob job; LiveCapture live; long extra; int duration;
        await _gate.WaitAsync(token);
        try
        {
            job = FindJob(id, revision);
            if (job.State != JobState.Recording || seconds is < 1 or > 3600 || job.DurationSeconds + seconds > 43200) throw new CommandException(409, "Only active recordings can be extended, up to a 12-hour total.");
            live = _live[id];
            if (live.CommandPending) throw new CommandException(409, "A channel command is already in progress.");
            extra = checked(job.Configuration.Capture.ReservationBytesPerSecond * seconds * 2L);
            if (FreeSpace(job.Configuration.CacheRoot) - _jobs.Values.Sum(j => j.ReservedBytes) - extra < job.Configuration.EmergencyReserveBytes) throw new CommandException(409, "Insufficient cache space for extension.");
            // Reserve before the asynchronous acknowledgement so another channel cannot spend the same space.
            job.ReservedBytes += extra; live.CommandPending = true; duration = job.DurationSeconds + seconds;
        }
        finally { _gate.Release(); }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await live.Session.UpdateDurationAsync(duration, timeout.Token);
            await _gate.WaitAsync();
            try
            {
                if (job.State != JobState.Recording) throw new CommandException(409, "Recording ended while extension was being acknowledged.");
                job.DurationSeconds = duration; job.Revision++; Save(job); return Clone(job);
            }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            await _gate.WaitAsync();
            try
            {
                if (job.State == JobState.Recording)
                {
                    job.Warnings.Add("Duration acknowledgement failed; recording finalized for review.");
                    BeginFinalizationUnderGate(job, "Duration extension acknowledgement failed");
                }
            }
            finally { _gate.Release(); }
            throw new CommandException(409, e.Message);
        }
        finally
        {
            await _gate.WaitAsync();
            try { live.CommandPending = false; if (job.ReservedBytes >= extra && job.State != JobState.Recording) job.ReservedBytes -= extra; }
            finally { _gate.Release(); }
        }
    }
    private RecordingJob FindJob(string id, long revision)
    {
        var job = _jobs.GetValueOrDefault(id) ?? throw new CommandException(404, "Recording not found.");
        if (job.Revision != revision) throw new CommandException(409, "Job changed. Refresh before sending another command.");
        return job;
    }
    private static bool IsContent(CaptureMetrics metrics) => SignalObservationPolicy.IsContent(metrics);
    private void Alert(string severity, string message)
    {
        if (_alerts.LastOrDefault() is { } last && last.Message == message && last.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5)) return;
        var alert = new OperationalAlert(Guid.NewGuid().ToString("N"), severity, message, DateTimeOffset.UtcNow);
        _alerts.Add(alert); if (_alerts.Count > 100) _alerts.RemoveAt(0); _store.Save("alert", alert.Id, alert);
        _notifications.Queue($"DeckCapture {severity}", message, "alert-" + alert.Id);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _capabilities = await _capture.ProbeAsync(_configuration.Capture, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(stoppingToken);
                try { TickUnderGate(); }
                finally { _gate.Release(); }
                if (_notificationTask.IsCompleted)
                {
                    try { await _notificationTask; } catch (Exception e) { _logger.LogError(e, "Notification outbox processing failed"); }
                    _notificationTask = _notifications.ProcessAsync(_configuration.Notifications, stoppingToken);
                }
                await Task.Delay(250, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { _logger.LogError(e, "Supervisor tick failed"); await Task.Delay(1000, stoppingToken); }
        }
    }
    private void TickUnderGate()
    {
        ScheduleAutomaticMonitoringUnderGate();
        foreach (var (id, live) in _live.ToArray())
        {
            var job = _jobs[id]; var metrics = live.Session.Metrics;
            if (job.State is JobState.Prepared or JobState.Waiting && !live.CommandPending && live.Session.Completion.IsCompleted)
            {
                var completed = live.Session.Completion;
                var reason = completed.IsCompletedSuccessfully ? completed.Result.Error : null;
                job.State = JobState.Failed; job.Error = string.IsNullOrWhiteSpace(reason)
                    ? "Input monitoring ended before recording. Prepare a new job; retained spool is available for review." : reason;
                job.ReservedBytes = 0; job.Revision++; job.FinishedAt = DateTimeOffset.UtcNow; Save(job);
                _live.Remove(id); _controlTasks.Add(Task.Run(async () => await live.Session.DisposeAsync()));
                Alert("Warning", $"{job.FileName}: {job.Error}"); continue;
            }
            if (job.State == JobState.Waiting && !live.CommandPending)
            {
                if (!SignalObservationPolicy.HasFreshAnalysis(metrics)) live.ContentSince = null;
                else if (!IsContent(metrics)) { live.SawNonContent = true; live.ContentSince = null; }
                else if (live.SawNonContent)
                {
                    live.ContentSince ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(live.ContentSince.Value).TotalSeconds >= job.Configuration.Detection.StartConfirmationSeconds) _ = BeginUnderGate(job);
                }
                // The worker owns the monotonic monitoring deadline and reports its expiry.
                // Never relabel an expiring/dead input as ready for a manual start.
            }
            if (job.State != JobState.Recording) continue;
            job.RecordedSeconds = Stopwatch.GetElapsedTime(live.StartTimestamp).TotalSeconds;
            RecordIntegrityWarningsUnderGate(job, metrics);
            if (SignalObservationPolicy.LossWarning(metrics) is { } loss && !live.WarnedLoss)
            { live.WarnedLoss = true; job.Warnings.Add(loss); Save(job); Alert("Warning", $"{job.FileName}: {loss}"); }
            if (metrics.DroppedFrames > 0 && !job.Warnings.Contains("Capture pipeline reported dropped frames.")) { job.Warnings.Add("Capture pipeline reported dropped frames."); Save(job); Alert("Error", $"{job.FileName}: capture pipeline reported dropped frames."); }
            var detect = _configuration.ExperimentalEndDetection && job.ExperimentalEndDetectionActive && job.EndMode != "Off";
            var threshold = job.Configuration.Detection;
            if (detect && job.RecordedSeconds >= threshold.MinimumRecordedSeconds && SignalObservationPolicy.CanAnalyzeContent(metrics) && metrics.BlackRatio >= threshold.BlackRatio && metrics.AudioRmsDbfs <= threshold.SilenceDbfs)
            {
                live.BlackSince ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(live.BlackSince.Value).TotalSeconds >= threshold.BlackSeconds)
                {
                    if (!live.WarnedEnd) { live.WarnedEnd = true; job.Warnings.Add("Suspected tape ending: extended black and silence."); Save(job); Alert("Warning", $"{job.FileName}: suspected tape ending."); }
                    if (job.EndMode == "Stop")
                    {
                        live.GraceSince ??= Stopwatch.GetTimestamp();
                        if (Stopwatch.GetElapsedTime(live.GraceSince.Value).TotalSeconds >= threshold.GraceSeconds) { BeginFinalizationUnderGate(job, "Experimental tape-end stop"); continue; }
                    }
                }
            }
            else { live.BlackSince = null; live.GraceSince = null; }
            if ((!live.CommandPending && job.RecordedSeconds >= job.DurationSeconds) || live.Session.Completion.IsCompleted) BeginFinalizationUnderGate(job, "Duration reached");
            else if (FreeSpace(job.Configuration.CacheRoot) < _configuration.EmergencyReserveBytes) { job.Warnings.Add("Capture finalized to protect remaining cache capacity."); BeginFinalizationUnderGate(job, "Low cache space"); Alert("Error", "Local media cache reached its emergency reserve."); }
        }
        foreach (var completed in _controlTasks.Where(t => t.IsCompleted).ToArray())
        {
            if (completed.Exception is { } error) _logger.LogWarning(error, "Asynchronous channel command failed");
            _controlTasks.Remove(completed);
        }
        _finalizers.RemoveAll(t => t.IsCompletedSuccessfully);
        TickTransfersUnderGate();
        if (_recordingGroup is not null && _recordingGroup.All(id => !IsActive(_jobs[id].State)))
        {
            var ids = _recordingGroup.Order().ToArray(); var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids))));
            if (_configuration.Notifications.CaptureGroupComplete) { var report = BuildReport("Capture group complete", _recordingGroup); _notifications.Queue(report.Subject, report.Body, "capture-" + key); }
            _recordingGroup = null;
            _store.Save("capture-group", "current", new CaptureGroupState([]));
        }
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(_configuration.TimeZone));
        if (_configuration.Notifications.DailyDigestLocalTime is { } daily && TimeOnly.TryParseExact(daily, "HH:mm", out var due) && TimeOnly.FromDateTime(now.DateTime) >= due && _lastDailyDate != now.ToString("yyyy-MM-dd"))
        {
            var date = now.ToString("yyyy-MM-dd");
            var report = BuildReport("Daily digest"); _notifications.Queue(report.Subject, report.Body, "daily-" + date);
            _store.Save("daily-report", "last", new DailyReportState(date)); _lastDailyDate = date;
        }
    }
    private void BeginFinalizationUnderGate(RecordingJob job, string reason)
    {
        if (job.State != JobState.Recording) return;
        job.State = JobState.Finalizing; job.StopReason = reason; job.Revision++; Save(job);
        _finalizers.Add(Task.Run(() => FinalizeAsync(job.Id)));
    }
    private async Task FinalizeAsync(string id)
    {
        RecordingJob job; ICaptureSession session;
        var sessionReleased = false;
        await _gate.WaitAsync();
        try { job = _jobs[id]; session = _live[id].Session; }
        finally { _gate.Release(); }
        try
        {
            var result = await session.StopAsync(CancellationToken.None);
            if (!result.Success || result.MediaPath is null) throw new InvalidDataException(result.Error ?? "Capture did not produce a valid master.");
            await _gate.WaitAsync();
            try
            {
                // The last stderr/metrics event may arrive during StopAsync, after the final recording tick.
                RecordIntegrityWarningsUnderGate(job, session.Metrics);
                if (result.DroppedFrames > 0 && !job.Warnings.Contains("Capture pipeline reported dropped frames.")) job.Warnings.Add("Capture pipeline reported dropped frames.");
                job.RecordedSeconds = result.DurationSeconds;
            }
            finally { _gate.Release(); }
            // Mandatory stop/validation has returned. Release this exact input owner before
            // optional QC or package hashing; those retain the media and disk reservation.
            // Keep Finalizing/exclusive admission until disposal has actually completed.
            await session.DisposeAsync();
            sessionReleased = true;
            RecordingJob snapshot;
            await _gate.WaitAsync();
            try
            {
                if (_live.TryGetValue(id, out var current) && ReferenceEquals(current.Session, session)) _live.Remove(id);
                job.State = JobState.Validating; Save(job); snapshot = Clone(job);
            }
            finally { _gate.Release(); }
            object? analysis = null;
            if (snapshot.Configuration.Capture.OfflineQcEnabled)
            {
                void UpdateProgress(OfflineQcProgress progress) => RecordQcProgress(id, new(progress.Phase,
                    progress.Frames, progress.ProcessedSeconds, progress.DurationSeconds, progress.UpdatedAt));
                analysis = OfflineQcAnalyzer is { } analyze
                    ? await analyze(result.MediaPath, snapshot.Configuration.Capture, _lifetime.Token, UpdateProgress)
                    : await OfflineQcService.AnalyzeAsync(result.MediaPath, snapshot.Configuration.Capture, null, _lifetime.Token,
                        UpdateProgress);
            }
            RecordQcProgress(id, new("Sealing", result.Frames, result.DurationSeconds, result.DurationSeconds, DateTimeOffset.UtcNow));
            snapshot.QcProgress = job.QcProgress;
            snapshot.State = snapshot.Warnings.Count == 0 ? JobState.Complete : JobState.NeedsReview;
            snapshot.FinishedAt = DateTimeOffset.UtcNow; snapshot.ReservedBytes = 0;
            var metadata = new { Job = snapshot, Result = result, Limitations = "See CAPTURE.md for adapter, pre-roll, timing, ancillary-data, and hardware validation limits. This snapshot is sealed only when the checksums and delivery manifest are complete; the supervisor database is authoritative." };
            await WriteNewJsonAsync(Path.Combine(job.PackageDirectory, "metadata.json"), metadata);
            var qc = new { Validated = true, result.DurationSeconds, result.Frames, result.DroppedFrames, Warnings = snapshot.Warnings, CheckedAt = DateTimeOffset.UtcNow, Analysis = analysis, QctoolsReport = "Native DeckCapture JSON; not a QCTools XML report." };
            await WriteNewJsonAsync(Path.Combine(job.PackageDirectory, "qc-summary.json"), qc);
            var artifacts = new List<ArtifactFile>();
            foreach (var file in Directory.EnumerateFiles(job.PackageDirectory).Order())
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                artifacts.Add(new(Path.GetFileName(file), stream.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream))));
            }
            var hashes = string.Join('\n', artifacts.Select(a => $"{a.Sha256}  {a.RelativePath}")) + "\n";
            var hashPath = Path.Combine(job.PackageDirectory, "checksums.sha256");
            await using (var output = new FileStream(hashPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(output)) await writer.WriteAsync(hashes);
            await using (var hashStream = new FileStream(hashPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                artifacts.Add(new("checksums.sha256", hashStream.Length, Convert.ToHexString(await SHA256.HashDataAsync(hashStream))));
            await _gate.WaitAsync();
            try
            {
                job.Artifacts = artifacts; job.State = job.Warnings.Count == 0 ? JobState.Complete : JobState.NeedsReview;
                job.QcProgress = new("Complete", result.Frames, result.DurationSeconds, result.DurationSeconds, DateTimeOffset.UtcNow);
                job.ReservedBytes = 0; job.FinishedAt = DateTimeOffset.UtcNow; job.Revision++;
                var delivery = NewDelivery(job); _deliveries.Add(delivery.Id, delivery);
                _store.SaveBatch([("recording", job.Id, JsonSerializer.Serialize(job, StateStore.Json)), ("delivery", delivery.Id, JsonSerializer.Serialize(delivery, StateStore.Json))]);
            }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            await _gate.WaitAsync(); try { job.State = JobState.Failed; job.Error = e.Message; job.ReservedBytes = 0; job.FinishedAt = DateTimeOffset.UtcNow; job.Revision++; Save(job); Alert("Error", $"{job.FileName}: finalization failed: {e.Message}"); } finally { _gate.Release(); }
        }
        finally
        {
            try { if (!sessionReleased) { await session.DisposeAsync(); sessionReleased = true; } }
            catch (Exception e) { _logger.LogError(e, "Capture session {Id} disposal failed; retained media requires review", id); }
            finally
            {
                await _gate.WaitAsync();
                try
                {
                    // Never clear a replacement owner, or admit another open after failed cleanup.
                    if (sessionReleased && _live.TryGetValue(id, out var current) && ReferenceEquals(current.Session, session)) _live.Remove(id);
                }
                finally { _gate.Release(); }
            }
        }
    }
    private void RecordQcProgress(string id, QcProgressSnapshot progress)
    {
        // The analyzer calls outside the ownership gate and throttles observations. Persist
        // only this job; a later capture on the same channel has independent state/revision.
        _gate.Wait();
        try
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State != JobState.Validating) return;
            var previous = job.QcProgress;
            job.QcProgress = progress;
            Save(job);
            if (previous?.Phase != progress.Phase || (long)(previous.ProcessedSeconds / 30) != (long)(progress.ProcessedSeconds / 30))
                _logger.LogInformation("QC {Id}: {Phase}, {Frames} frames, {ProcessedSeconds:F1}/{DurationSeconds} seconds",
                    id, progress.Phase, progress.Frames, progress.ProcessedSeconds, progress.DurationSeconds);
        }
        finally { _gate.Release(); }
    }
    private static async Task WriteNewJsonAsync(string path, object value)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await JsonSerializer.SerializeAsync(output, value, StateStore.Json);
        await output.FlushAsync();
    }
    private static DeliveryJob NewDelivery(RecordingJob job) => new()
    {
        RecordingId = job.Id, FileName = job.FileName, SourceDirectory = job.PackageDirectory, DestinationRoot = job.DestinationRoot,
        RelativeDirectory = Path.Combine(job.RelativeDirectory, job.FileName), Files = job.Artifacts,
        TotalBytes = job.Artifacts.Sum(a => a.Size), State = job.State == JobState.NeedsReview ? DeliveryState.Held : DeliveryState.Queued
    };
    public async Task<string?> PreviewPathAsync(string channelId)
    {
        await _gate.WaitAsync();
        try
        {
            var pair = _live.FirstOrDefault(pair => _jobs[pair.Key].ChannelId == channelId && OwnsInputUnderGate(_jobs[pair.Key]) && !pair.Value.Session.Completion.IsCompleted);
            if (pair.Value?.Session.Metrics.PreviewPath is { } jobPath)
                return LivePreviewFiles.ResolveJpeg(_jobs[pair.Key].Configuration.CacheRoot, pair.Key, jobPath);
            if (_monitors.TryGetValue(channelId, out var monitor) && !monitor.Closing && monitor.Session is { } session &&
                !session.Completion.IsCompleted && session.Metrics.PreviewPath is { } path)
                return LivePreviewFiles.ResolveJpeg(monitor.CacheRoot, monitor.Id, path);
            return null;
        }
        finally { _gate.Release(); }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _shuttingDown = true;
            foreach (var job in _jobs.Values.Where(j => j.State == JobState.Recording).ToArray()) BeginFinalizationUnderGate(job, "Supervisor shutdown");
            foreach (var transfer in _transfers.Values) transfer.Cancel.Cancel();
            foreach (var opening in _opening.Values) opening.Cancel.Cancel();
            foreach (var monitor in _monitors.Values) { monitor.Closing = true; monitor.CancelInput(); }
        }
        finally { _gate.Release(); }
        try
        {
            await base.StopAsync(cancellationToken);
            await _automaticMonitorAttempt.WaitAsync(TimeSpan.FromSeconds(55), CancellationToken.None);
            IdleMonitor[] monitors;
            await _gate.WaitAsync(CancellationToken.None); try { monitors = _monitors.Values.ToArray(); } finally { _gate.Release(); }
            await Task.WhenAll(monitors.Select(CloseMonitorAsync)).WaitAsync(TimeSpan.FromSeconds(55), CancellationToken.None);
            Task[] openings;
            await _gate.WaitAsync(); try { openings = _opening.Values.Select(o => o.Finished.Task).ToArray(); } finally { _gate.Release(); }
            await Task.WhenAll(openings).WaitAsync(TimeSpan.FromSeconds(55), CancellationToken.None);
            try { await Task.WhenAll(_controlTasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(25), CancellationToken.None); }
            catch (Exception e) { _logger.LogWarning(e, "Channel command interrupted during shutdown"); }
            await Task.WhenAll(_finalizers.ToArray()).WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
        }
        catch (TimeoutException e) { _logger.LogError(e, "Finalization exceeded shutdown grace; media remains for recovery"); }
        finally
        {
            _lifetime.Cancel();
            Task[] transfers; ICaptureSession[] sessions;
            await _gate.WaitAsync();
            try { transfers = _transfers.Values.Select(t => t.Task).ToArray(); sessions = _live.Values.Select(l => l.Session).ToArray(); }
            finally { _gate.Release(); }
            try { await Task.WhenAll(transfers.Append(_notificationTask)).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
            catch (Exception e) { _logger.LogWarning(e, "Background work did not finish during shutdown; recovery state is retained"); }
            foreach (var session in sessions)
                try { await session.DisposeAsync(); } catch (Exception e) { _logger.LogError(e, "Worker cleanup failed"); }
        }
    }
}
