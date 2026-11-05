using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed partial class CaptureSupervisor
{
    private Task _automaticMonitorAttempt = Task.CompletedTask;
    private readonly Dictionary<string, long> _automaticMonitorRetryAt = new(StringComparer.Ordinal);
    private long _nextAutomaticMonitorAttemptAt;
    internal TimeProvider MonitorTimeProvider { get; set; } = TimeProvider.System;

    private long MonitorDeadline(TimeSpan delay) => MonitorTimeProvider.GetTimestamp() + (long)(delay.TotalSeconds * MonitorTimeProvider.TimestampFrequency);
    private static CaptureProfile MonitorProfile(CaptureProfile profile, bool automatic) => profile with
        { PreRollSeconds = 0, MonitoringTimeoutMinutes = automatic ? 0 : profile.MonitoringTimeoutMinutes };

    // Called under the state gate. Only one automatic open may be in flight; never await
    // driver/media I/O here. A failed input cannot hold up capture timers or the next channel.
    private void ScheduleAutomaticMonitoringUnderGate()
    {
        var now = MonitorTimeProvider.GetTimestamp();
        if (_shuttingDown || MaintenanceActive || _inputHardwareProbeActive || !_automaticMonitorAttempt.IsCompleted || now < _nextAutomaticMonitorAttemptAt) return;
        foreach (var monitor in _monitors.Values.Where(monitor => monitor.Automatic && !monitor.Closing && monitor.Session is not null))
        {
            // Only an actual negative report qualifies. Unknown lock or unavailable scalar
            // analysis alone cannot justify repeatedly interrupting a usable A/V preview.
            if (monitor.Session!.Metrics.InputSignalPresent != false) { monitor.NoInputSince = null; continue; }
            monitor.NoInputSince ??= now;
            if (MonitorTimeProvider.GetElapsedTime(monitor.NoInputSince.Value, now) < TimeSpan.FromSeconds(15)) continue;
            monitor.Closing = true; monitor.CancelInput();
            _automaticMonitorRetryAt[monitor.Channel.Id] = MonitorDeadline(TimeSpan.FromSeconds(15));
            _automaticMonitorAttempt = RetireUnavailableMonitorAsync(monitor);
            return;
        }
        var channel = _configuration.Channels.FirstOrDefault(channel =>
            !_opening.Values.Any(opening => opening.ChannelId == channel.Id) &&
            !_jobs.Values.Any(job => job.ChannelId == channel.Id && OwnsInputUnderGate(job)) &&
            !_live.Keys.Any(id => _jobs[id].ChannelId == channel.Id) &&
            (!_monitors.TryGetValue(channel.Id, out var monitor) || !monitor.Busy) &&
            (!_automaticMonitorRetryAt.TryGetValue(channel.Id, out var retry) || now >= retry));
        if (channel is null) return;
        _nextAutomaticMonitorAttemptAt = MonitorDeadline(TimeSpan.FromSeconds(1));
        _automaticMonitorAttempt = Task.Run(() => AttemptAutomaticMonitorAsync(channel.Id));
    }

    private async Task RetireUnavailableMonitorAsync(IdleMonitor monitor)
    {
        // Yield before acquiring CloseMonitorAsync's state gate; our caller holds that gate.
        await Task.Yield();
        try { await CloseMonitorAsync(monitor); }
        catch (Exception exception) { _logger.LogWarning(exception, "Unavailable monitor cleanup failed for {Channel}", monitor.Channel.Id); }
    }

    private async Task AttemptAutomaticMonitorAsync(string channelId)
    {
        try { await StartMonitorCoreAsync(channelId, CancellationToken.None, automatic: true); }
        catch (Exception exception) { _logger.LogDebug(exception, "Automatic input monitoring attempt deferred for {Channel}", channelId); }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                var channel = _configuration.Channels.FirstOrDefault(channel => channel.Id == channelId);
                if (channel is not null)
                {
                    var unavailable = !_capabilities.Available || channel.SourceKind == "DeckLink" && !_capabilities.DeckLinkSupported;
                    _automaticMonitorRetryAt[channelId] = MonitorDeadline(TimeSpan.FromSeconds(unavailable ? 30 : 15));
                }
                _nextAutomaticMonitorAttemptAt = MonitorDeadline(TimeSpan.FromSeconds(1));
            }
            finally { _gate.Release(); }
        }
    }

    // Exercise the production scheduler deterministically without running notifications or transfers.
    internal async Task RunAutomaticMonitoringPassAsync()
    {
        Task attempt;
        await _gate.WaitAsync();
        try { ScheduleAutomaticMonitoringUnderGate(); attempt = _automaticMonitorAttempt; }
        finally { _gate.Release(); }
        await attempt;
    }
}
