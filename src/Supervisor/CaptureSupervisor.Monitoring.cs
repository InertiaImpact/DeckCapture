using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed record ChannelMonitoring(string Id, string State, string Source, DateTimeOffset StartedAt, string? Error = null);

public sealed partial class CaptureSupervisor
{
    private sealed class IdleMonitor(string id, ChannelConfiguration channel, CaptureProfile profile, string cacheRoot, long reservedBytes, CancellationToken lifetime, bool automatic)
    {
        public string Id { get; } = id;
        public ChannelConfiguration Channel { get; } = channel;
        public CaptureProfile Profile { get; } = profile;
        public bool Automatic { get; } = automatic;
        public string CacheRoot { get; } = cacheRoot;
        public long ReservedBytes { get; set; } = reservedBytes;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public ICaptureSession? Session { get; set; }
        public string? Error { get; set; }
        public bool Closing { get; set; }
        public long? NoInputSince { get; set; }
        public CancellationTokenSource Cancel { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim CloseGate { get; } = new(1, 1);
        public void CancelInput() { try { Cancel.Cancel(); } catch (ObjectDisposedException) { /* Already closed. */ } }
        public bool Busy => Session is not null || !Opened.Task.IsCompleted || Closing;
        public ChannelMonitoring Descriptor => new(Id, Error is not null ? "Failed" : Session is null ? "Opening" : "Live", "Monitor", StartedAt, Error);
    }
    // Ephemeral ownership only. Never restored from StateStore or represented as a RecordingJob.
    private readonly Dictionary<string, IdleMonitor> _monitors = new(StringComparer.Ordinal);
    private bool MonitorsBusy => _monitors.Values.Any(monitor => monitor.Busy);
    private long MonitorReservedBytes => _monitors.Values.Sum(monitor => monitor.ReservedBytes);

    private ChannelMonitoring? MonitoringUnderGate(string channelId)
    {
        var job = _jobs.Values.FirstOrDefault(job => job.ChannelId == channelId && OwnsInputUnderGate(job));
        if (job is not null) return new(job.Id, _live.ContainsKey(job.Id) ? "Live" : "Opening", "RecordingJob", job.PreparedAt);
        return _monitors.TryGetValue(channelId, out var monitor) ? monitor.Descriptor : null;
    }
    private void EnsureInputUnownedUnderGate(ChannelConfiguration channel, bool allowOwnMonitor)
    {
        bool Same(ChannelConfiguration other) => channel.SourceKind == "DeckLink" && other.SourceKind == "DeckLink" &&
            channel.DeviceName.Equals(other.DeviceName, StringComparison.OrdinalIgnoreCase);
        if (_jobs.Values.Any(job => job.ChannelId != channel.Id && OwnsInputUnderGate(job) && Same(job.Channel)) ||
            _live.Keys.Any(id => _jobs[id].ChannelId == channel.Id || Same(_jobs[id].Channel)) ||
            _monitors.Values.Any(monitor => monitor.Busy && (!allowOwnMonitor || monitor.Channel.Id != channel.Id) &&
                (monitor.Channel.Id == channel.Id || Same(monitor.Channel))))
            throw new CommandException(409, "This input is already being monitored or captured by another channel. Stop that idle monitor first.");
    }

    public Task<ChannelMonitoring> StartMonitorAsync(string channelId, CancellationToken token) => StartMonitorCoreAsync(channelId, token, automatic: false);

    private async Task<ChannelMonitoring> StartMonitorCoreAsync(string channelId, CancellationToken token, bool automatic)
    {
        IdleMonitor monitor;
        await _gate.WaitAsync(token);
        try
        {
            if (_shuttingDown || MaintenanceActive) throw new CommandException(409, "Monitoring is unavailable during shutdown or maintenance.");
            EnsureInputDiscoveryIdleUnderGate();
            var channel = _configuration.Channels.FirstOrDefault(channel => channel.Id == channelId) ?? throw new CommandException(404, "Channel not found.");
            var current = MonitoringUnderGate(channelId);
            if (current?.Source == "RecordingJob") return current;
            if (_monitors.TryGetValue(channelId, out var existing) && existing.Busy)
            {
                if (existing.Closing) throw new CommandException(409, "Monitoring is stopping. Wait for it to finish before starting again.");
                return existing.Descriptor;
            }
            existing?.Cancel.Dispose();
            EnsureInputUnownedUnderGate(channel, allowOwnMonitor: false);
            var reserved = checked(_configuration.Capture.ReservationBytesPerSecond * 4 + 16L * 1024 * 1024);
            if (FreeSpace(_configuration.CacheRoot) - _jobs.Values.Sum(job => job.ReservedBytes) - MonitorReservedBytes - reserved < _configuration.EmergencyReserveBytes)
                throw new CommandException(409, "Insufficient unreserved cache space for bounded input monitoring.");
            monitor = new(Guid.NewGuid().ToString("N"), Clone(channel), MonitorProfile(Clone(_configuration.Capture), automatic),
                _configuration.CacheRoot, reserved, _lifetime.Token, automatic);
            _monitors[channelId] = monitor;
        }
        finally { _gate.Release(); }
        ICaptureSession? opened = null;
        try
        {
            var spool = PathSafety.UnderRoot(monitor.CacheRoot, Path.Combine(".spool", monitor.Id));
            Directory.CreateDirectory(spool);
            monitor.Cancel.CancelAfter(TimeSpan.FromSeconds(45));
            opened = await _capture.OpenAsync(new(monitor.Channel, monitor.Profile, spool) { MonitorOnly = true }, monitor.Cancel.Token);
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (monitor.Closing || monitor.Cancel.IsCancellationRequested || _shuttingDown)
                    throw new OperationCanceledException("Monitoring start was canceled.");
                monitor.Cancel.CancelAfter(Timeout.InfiniteTimeSpan);
                monitor.Session = opened; opened = null;
            }
            finally { _gate.Release(); }
            _ = WatchMonitorAsync(monitor, monitor.Session!);
            return monitor.Descriptor;
        }
        catch (Exception exception)
        {
            if (opened is not null) await opened.DisposeAsync();
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                monitor.Error = monitor.Closing ? "Monitoring was stopped." : "Monitoring could not start: " + exception.Message; monitor.ReservedBytes = 0;
                _automaticMonitorRetryAt[channelId] = MonitorDeadline(TimeSpan.FromSeconds(15));
            }
            finally { _gate.Release(); }
            return monitor.Descriptor;
        }
        finally { monitor.Opened.TrySetResult(); }
    }

    public async Task<object> StopMonitorAsync(string channelId, CancellationToken token)
    {
        IdleMonitor? monitor;
        await _gate.WaitAsync(token);
        try
        {
            if (_jobs.Values.Any(job => job.ChannelId == channelId && OwnsInputUnderGate(job)))
                throw new CommandException(409, "This channel is monitoring with a prepared or active recording. Use its recording controls; monitor stop cannot stop a recording.");
            _monitors.TryGetValue(channelId, out monitor);
            if (monitor is null) return new { Stopped = true };
            monitor.Closing = true; monitor.CancelInput();
        }
        finally { _gate.Release(); }
        await CloseMonitorAsync(monitor);
        return new { Stopped = true };
    }

    private async Task CloseMonitorAsync(IdleMonitor monitor)
    {
        await monitor.CloseGate.WaitAsync();
        try
        {
            monitor.CancelInput();
            await monitor.Opened.Task;
            if (monitor.Session is { } session) await session.DisposeAsync();
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                monitor.Session = null; monitor.ReservedBytes = 0; monitor.Closing = false;
                if (_monitors.TryGetValue(monitor.Channel.Id, out var current) && ReferenceEquals(current, monitor)) _monitors.Remove(monitor.Channel.Id);
            }
            finally { _gate.Release(); }
            monitor.Cancel.Dispose();
        }
        finally { monitor.CloseGate.Release(); }
    }

    private async Task WatchMonitorAsync(IdleMonitor monitor, ICaptureSession session)
    {
        try
        {
            await monitor.Opened.Task;
            var result = await session.Completion;
            await monitor.CloseGate.WaitAsync();
            try
            {
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (monitor.Closing || !ReferenceEquals(monitor.Session, session)) return;
                    monitor.Closing = true;
                    _automaticMonitorRetryAt[monitor.Channel.Id] = MonitorDeadline(TimeSpan.FromSeconds(15));
                    monitor.Error = result.Error?.Contains("Monitoring expired", StringComparison.OrdinalIgnoreCase) == true
                        ? $"Monitoring reached its configured {monitor.Profile.MonitoringTimeoutMinutes}-minute timeout. Start monitoring again when needed."
                        : result.Error ?? "Input monitoring ended. Start monitoring again when needed.";
                }
                finally { _gate.Release(); }
                await session.DisposeAsync();
                await _gate.WaitAsync(CancellationToken.None);
                try { monitor.Session = null; monitor.ReservedBytes = 0; monitor.Closing = false; }
                finally { _gate.Release(); }
                monitor.Cancel.Dispose();
            }
            finally { monitor.CloseGate.Release(); }
        }
        catch (Exception exception)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { monitor.Error = "Input monitoring ended: " + exception.Message; monitor.Closing = false; }
            finally { _gate.Release(); }
        }
    }
}
