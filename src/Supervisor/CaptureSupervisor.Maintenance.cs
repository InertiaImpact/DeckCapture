using DeckCapture.Domain;
namespace DeckCapture.Supervisor;
public sealed partial class CaptureSupervisor
{
    private DateTimeOffset? _maintenanceUntil = Environment.GetEnvironmentVariable("DECKCAPTURE_MAINTENANCE_START") == "1" ? DateTimeOffset.UtcNow.AddMinutes(10) : null;
    private bool _maintenanceDraining;
    private bool MaintenanceActive => _maintenanceDraining || _maintenanceUntil > DateTimeOffset.UtcNow;
    public async Task<object> SetMaintenanceAsync(bool enabled, CancellationToken token)
    {
        IdleMonitor[] monitors;
        await _gate.WaitAsync(token);
        try
        {
            if (_maintenanceDraining) throw new CommandException(409, "Input monitoring is still draining for maintenance.");
            if (enabled && (_jobs.Values.Any(j => IsActive(j.State)) || _opening.Count > 0 || _live.Count > 0 || _transfers.Count > 0 || _inputHardwareProbeActive))
                throw new CommandException(409, "Maintenance requires an idle recorder with no prepared jobs or active transfers.");
            // Reserve before releasing the gate: automatic attempts and preparation cannot race the drain.
            _maintenanceUntil = enabled ? DateTimeOffset.UtcNow.AddMinutes(10) : null;
            monitors = enabled ? _monitors.Values.ToArray() : [];
            _maintenanceDraining = enabled;
            foreach (var monitor in monitors) { monitor.Closing = true; monitor.CancelInput(); }
        }
        finally { _gate.Release(); }
        try { await Task.WhenAll(monitors.Select(CloseMonitorAsync)).WaitAsync(TimeSpan.FromSeconds(55), token); }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { _maintenanceDraining = false; }
            finally { _gate.Release(); }
        }
        return new { Enabled = enabled, Until = enabled ? _maintenanceUntil : null };
    }
    public async Task<DiagnosticsDownload> DiagnosticsBundleAsync(DevelopmentDiagnostics diagnostics, CancellationToken token)
    {
        AppConfiguration configuration; CaptureCapabilities capabilities; DiagnosticsEvidenceSource[] evidence;
        await _gate.WaitAsync(token);
        try
        {
            configuration = Clone(_configuration); capabilities = _capabilities;
            evidence = _jobs.Values.Select(job => (At: job.PreparedAt, Source: new DiagnosticsEvidenceSource(job.Id, job.Configuration.CacheRoot)))
                .Concat(_monitors.Values.Select(monitor => (At: monitor.StartedAt, Source: new DiagnosticsEvidenceSource(monitor.Id, monitor.CacheRoot))))
                .OrderByDescending(item => item.At).Take(10).Select(item => item.Source).ToArray();
        }
        finally { _gate.Release(); }
        return await diagnostics.CreateBundleAsync(configuration, capabilities, evidence, token);
    }
}
