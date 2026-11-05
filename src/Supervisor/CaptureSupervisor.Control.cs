using System.Text;
using System.Text.Json;
using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed partial class CaptureSupervisor
{
    public async Task<AppConfiguration> UpdateConfigurationAsync(AppConfiguration candidate, CancellationToken token)
    {
        var errors = ConfigurationRules.Validate(candidate);
        if (errors.Count > 0) throw new CommandException(400, string.Join(" ", errors));
        IdleMonitor[] retiringMonitors;
        AppConfiguration saved;
        await _gate.WaitAsync(token);
        try
        {
            foreach (var job in _jobs.Values.Where(OwnsInputUnderGate))
            {
                var before = _configuration.Channels.First(c => c.Id == job.ChannelId);
                var after = candidate.Channels.FirstOrDefault(c => c.Id == job.ChannelId);
                if (after != before) throw new CommandException(409, "An active or prepared channel cannot be remapped or removed.");
                if (before.SourceKind == "DeckLink" && InputWiringRules.AffectsDevice(_configuration, candidate, before.DeviceName))
                    throw new CommandException(409, "Physical wiring annotations for an active or prepared input cannot be changed until it is closed.");
            }
            Directory.CreateDirectory(PathSafety.UnderRoot(candidate.CacheRoot, ""));
            var old = _configuration; var next = Clone(candidate);
            retiringMonitors = _monitors.Values.Where(monitor =>
                next.Channels.FirstOrDefault(channel => channel.Id == monitor.Channel.Id) != monitor.Channel ||
                MonitorProfile(next.Capture, monitor.Automatic) != monitor.Profile || next.CacheRoot != monitor.CacheRoot ||
                (monitor.Channel.SourceKind == "DeckLink" && InputWiringRules.AffectsDevice(old, next, monitor.Channel.DeviceName))).ToArray();
            var records = new List<(string, string, string)>
            {
                ("configuration-history", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff"), JsonSerializer.Serialize(old, StateStore.Json)),
                ("configuration", "current", JsonSerializer.Serialize(next, StateStore.Json))
            };
            // This is an intentional exception to immutable job snapshots: the master disable is a kill switch.
            if (!next.ExperimentalEndDetection)
            {
                foreach (var job in _jobs.Values.Where(j => IsActive(j.State) && j.ExperimentalEndDetectionActive))
                {
                    job.ExperimentalEndDetectionActive = false; job.Revision++;
                    if (_live.TryGetValue(job.Id, out var live)) { live.GraceSince = null; live.BlackSince = null; }
                    records.Add(("recording", job.Id, JsonSerializer.Serialize(job, StateStore.Json)));
                }
            }
            _store.SaveBatch(records); _configuration = next;
            foreach (var monitor in retiringMonitors) { monitor.Closing = true; monitor.CancelInput(); }
            foreach (var key in _automaticMonitorRetryAt.Keys.Where(key => !next.Channels.Any(channel => channel.Id == key) ||
                next.Channels.First(channel => channel.Id == key) != old.Channels.FirstOrDefault(channel => channel.Id == key) || next.Capture != old.Capture).ToArray())
                _automaticMonitorRetryAt.Remove(key);
            _ = RefreshCapabilitiesAsync(next.Capture);
            saved = Clone(next);
        }
        finally { _gate.Release(); }
        await Task.WhenAll(retiringMonitors.Select(CloseMonitorAsync)).WaitAsync(TimeSpan.FromSeconds(55), token);
        return saved;
    }
    private async Task RefreshCapabilitiesAsync(CaptureProfile profile)
    {
        try { _capabilities = await _capture.ProbeAsync(profile, _lifetime.Token); }
        catch (Exception e) { _logger.LogWarning(e, "Capture capability refresh failed"); }
    }
    public async Task<RecordingJob> ReviewAsync(string id, long revision, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var job = FindJob(id, revision);
            if (job.State != JobState.NeedsReview) throw new CommandException(409, "Only a validated recording awaiting review can be released.");
            job.State = JobState.Complete; job.Revision++; Save(job);
            foreach (var delivery in _deliveries.Values.Where(d => d.RecordingId == id && d.State == DeliveryState.Held)) { delivery.State = DeliveryState.Queued; Save(delivery); }
            return Clone(job);
        }
        finally { _gate.Release(); }
    }
    public async Task<DeliveryJob> DeliveryCommandAsync(string id, string action, bool enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var job = _deliveries.GetValueOrDefault(id) ?? throw new CommandException(404, "Delivery not found.");
            if (job.State == DeliveryState.Delivered) throw new CommandException(409, "Delivery is already verified and complete.");
            var reviewRequired = _jobs[job.RecordingId].State != JobState.Complete;
            switch (action)
            {
                case "override":
                    if (reviewRequired) throw new CommandException(409, "Review and release the recording before overriding its delivery.");
                    job.Override = enabled; break;
                case "hold":
                    if (!enabled && reviewRequired) throw new CommandException(409, "This recording requires QC review before delivery.");
                    if (enabled)
                    {
                        job.State = DeliveryState.Held;
                        if (_transfers.TryGetValue(id, out var active)) active.Cancel.Cancel();
                    }
                    else if (job.State == DeliveryState.Held) { job.State = DeliveryState.Queued; job.NextAttemptAt = null; }
                    break;
                case "retry":
                    if (reviewRequired) throw new CommandException(409, "Recording requires review.");
                    if (_transfers.ContainsKey(id)) throw new CommandException(409, "Delivery is still active.");
                    job.State = DeliveryState.Queued; job.NextAttemptAt = null; job.Error = null; job.Attempts = 0; break;
                default: throw new CommandException(400, "Unknown delivery action.");
            }
            Save(job); return Clone(job);
        }
        finally { _gate.Release(); }
    }
    public async Task<object> SetTransferOverrideAsync(bool enabled, int hours, CancellationToken token)
    {
        if (enabled && hours != 12) throw new CommandException(400, "The temporary override lasts 12 elapsed hours.");
        await _gate.WaitAsync(token);
        try
        {
            _overrideUntil = enabled ? DateTimeOffset.UtcNow.AddHours(12) : null;
            _store.Save("override", "current", new OverrideState(_overrideUntil));
            return new { OverrideUntil = _overrideUntil };
        }
        finally { _gate.Release(); }
    }
    private void TickTransfersUnderGate()
    {
        foreach (var pair in _transfers.Where(p => p.Value.Task.IsCompleted).ToArray()) { pair.Value.Cancel.Dispose(); _transfers.Remove(pair.Key); }
        var open = IsWindowOpen();
        if (_lastWindowOpen && !open && _configuration.Notifications.TransferWindowClosed)
        {
            var report = BuildReport("Transfer window closed", deliveredSince: _windowOpenedAt ?? DateTimeOffset.UtcNow);
            _notifications.Queue(report.Subject, report.Body, "window-" + _windowSession);
        }
        if (open != _lastWindowOpen || _windowSession is null)
        {
            if (open || _windowSession is null) { _windowSession = Guid.NewGuid().ToString("N"); _windowOpenedAt = DateTimeOffset.UtcNow; }
            _store.Save("window-state", "current", new WindowState(open, _windowSession, _windowOpenedAt)); _lastWindowOpen = open;
        }
        if (_overrideUntil is not null && !OverrideActive) { _overrideUntil = null; _store.Save("override", "current", new OverrideState(null)); }
        if (_shuttingDown || MaintenanceActive) return;
        var available = _configuration.Transfer.MaximumConcurrent - _transfers.Count;
        var candidates = _deliveries.Values.Where(d => d.State is DeliveryState.Queued or DeliveryState.RetryScheduled)
            .Where(d => d.NextAttemptAt is null || d.NextAttemptAt <= DateTimeOffset.UtcNow)
            .Where(d => _jobs[d.RecordingId].State == JobState.Complete && (open || OverrideActive || d.Override))
            .OrderByDescending(d => d.Override).ThenBy(d => d.CreatedAt).Take(Math.Max(0, available)).ToArray();
        foreach (var job in candidates)
        {
            job.State = DeliveryState.Copying; job.Attempts++; job.Error = null; Save(job);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var task = Task.Run(() => TransferAsync(job.Id, cancellation.Token), CancellationToken.None);
            _transfers.Add(job.Id, (task, cancellation));
        }
    }
    private string? _windowSession;
    private DateTimeOffset? _windowOpenedAt;
    private sealed record WindowState(bool WasOpen, string? Session, DateTimeOffset? OpenedAt = null);
    private DeliveryRates CurrentRates(string id)
    {
        _gate.Wait();
        try
        {
            // Overrides admit new work; they never bypass the outside-window bandwidth limit.
            var regular = IsWindowOpen();
            return new(regular ? _configuration.Transfer.PerTransferMiBps : _configuration.Transfer.OutsideWindowMiBps, _configuration.Transfer.AggregateMiBps);
        }
        finally { _gate.Release(); }
    }
    private async Task TransferAsync(string id, CancellationToken token)
    {
        DeliveryJob job;
        await _gate.WaitAsync(); try { job = _deliveries[id]; } finally { _gate.Release(); }
        try
        {
            var progress = new Progress<DeliveryProgress>(value =>
            {
                _gate.Wait();
                try
                {
                    if (job.State is not (DeliveryState.Copying or DeliveryState.Verifying)) return;
                    job.State = value.Phase.Equals("Verifying", StringComparison.OrdinalIgnoreCase) ? DeliveryState.Verifying : DeliveryState.Copying;
                    job.BytesCopied = value.BytesCopied; job.RateMiBps = value.RateMiBps;
                }
                finally { _gate.Release(); }
            });
            var request = new DeliveryTransferRequest(job.Id, job.SourceDirectory, job.DestinationRoot, job.RelativeDirectory,
                Path.Combine(StateDirectory, "checkpoints"), job.Files);
            var result = await _delivery.TransferAsync(request, () => CurrentRates(id), progress, token);
            if (!result.Verified) throw new InvalidDataException("Destination verification did not complete.");
            await _gate.WaitAsync();
            try { job.State = DeliveryState.Delivered; job.DeliveredAt = DateTimeOffset.UtcNow; job.DestinationPath = result.DestinationPath; job.BytesCopied = job.TotalBytes; job.RateMiBps = 0; job.Override = false; job.Error = null; Save(job); }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            await _gate.WaitAsync();
            try
            {
                if (job.State != DeliveryState.Held)
                {
                    job.State = job.Attempts >= 6 ? DeliveryState.Failed : DeliveryState.RetryScheduled;
                    job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, Math.Pow(2, job.Attempts) * 10));
                }
                job.RateMiBps = 0; job.Error = e is OperationCanceledException ? "Transfer paused or interrupted; verified resume is retained." : e.Message;
                Save(job); if (e is not OperationCanceledException) Alert("Error", $"Delivery {job.FileName} failed: {e.Message}");
            }
            finally { _gate.Release(); }
        }
    }
    private (string Subject, string Body) BuildReport(string kind, IReadOnlySet<string>? recordingIds = null, DateTimeOffset? deliveredSince = null)
    {
        var recordings = _jobs.Values.Where(j => recordingIds is null || recordingIds.Contains(j.Id)).ToArray();
        var deliveries = _deliveries.Values.Where(j => recordingIds is null || recordingIds.Contains(j.RecordingId))
            .Where(j => deliveredSince is null || j.State != DeliveryState.Delivered || j.DeliveredAt >= deliveredSince).ToArray();
        var text = new StringBuilder();
        text.AppendLine($"DeckCapture — {kind}"); text.AppendLine($"As of {DateTimeOffset.UtcNow:O}");
        if (deliveredSince is not null) text.AppendLine($"Delivered since {deliveredSince:O}; unfinished deliveries include the remaining queue. Running transfers continue at the outside-window rate.");
        text.AppendLine($"Recordings: {recordings.Count(j => j.State == JobState.Complete)} complete, {recordings.Count(j => j.State == JobState.NeedsReview)} need review, {recordings.Count(j => IsActive(j.State))} active.");
        text.AppendLine($"Delivery: {deliveries.Count(j => j.State == DeliveryState.Delivered)} delivered, {deliveries.Count(j => j.State is DeliveryState.Copying or DeliveryState.Verifying)} active, {deliveries.Count(j => j.State != DeliveryState.Delivered)} unfinished.");
        foreach (var job in recordings.OrderByDescending(j => j.PreparedAt)) text.AppendLine($"{job.FileName}: {job.State}; {job.RecordedSeconds:F2}s; {job.StopReason}; {string.Join("; ", job.Warnings)}");
        text.AppendLine("Deliveries:");
        foreach (var job in deliveries.OrderByDescending(j => j.CreatedAt)) text.AppendLine($"{job.FileName}: {job.State}; {job.BytesCopied}/{job.TotalBytes} bytes; {job.Error}");
        return ($"DeckCapture — {kind}", text.ToString());
    }
    public async Task<(string Subject, string Body)> ReportAsync(string kind)
    {
        await _gate.WaitAsync(); try { return BuildReport(kind); } finally { _gate.Release(); }
    }
    public async Task<object> QcAsync(string id)
    {
        string path; RecordingJob job;
        await _gate.WaitAsync(); try { job = Clone(_jobs.GetValueOrDefault(id) ?? throw new CommandException(404, "Recording not found.")); path = Path.Combine(job.PackageDirectory, "qc-summary.json"); } finally { _gate.Release(); }
        return new { Job = job, Summary = File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path)) : (JsonElement?)null };
    }
}
