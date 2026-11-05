using DeckCapture.Capture;
using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public sealed partial class CaptureSupervisor
{
    private bool _inputHardwareProbeActive;
    internal InputDiscovery InputDiscoveryService { get; set; } = new();

    // The probe reservation is already held before retiring monitor-only inputs.
    // Recording owners are rejected by the caller; automatic retries stay suspended.
    private IdleMonitor[] RetireIdleMonitorsForProbeUnderGate()
    {
        var monitors = _monitors.Values.ToArray();
        foreach (var monitor in monitors) { monitor.Closing = true; monitor.CancelInput(); }
        return monitors;
    }


    // PrepareAsync calls this under the same gate as setting this reservation. Never hold
    // the state gate during a potentially blocking driver probe: duration timers still run.
    private void EnsureInputDiscoveryIdleUnderGate()
    {
        if (_inputHardwareProbeActive) throw new CommandException(409, "An input hardware probe is running. Wait for it to finish before preparing a recording.");
    }

    public async Task<InputDiscoveryResult> DiscoverInputsAsync(CancellationToken token)
    {
        CaptureProfile profile;
        List<PhysicalInputWiring> wiring;
        await _gate.WaitAsync(token);
        try { profile = Clone(_configuration.Capture); wiring = Clone(_configuration.InputWiring); }
        finally { _gate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        return InputWiring.Apply(await InputDiscoveryService.DiscoverAsync(profile, linked.Token), wiring);
    }

    public async Task<InputFormatsResult> DiscoverInputFormatsAsync(string deviceName, CancellationToken token)
    {
        try { InputDiscovery.ValidateDeviceName(deviceName); }
        catch (ArgumentException exception) { throw new CommandException(400, exception.Message); }
        CaptureProfile profile;
        IdleMonitor[] monitors;
        await _gate.WaitAsync(token);
        try
        {
            if (_shuttingDown || MaintenanceActive) throw new CommandException(409, "Input probing is unavailable during shutdown or maintenance.");
            EnsureInputDiscoveryIdleUnderGate();
            if (_opening.Count > 0 || _live.Count > 0 || _jobs.Values.Any(job => OwnsInputUnderGate(job)) )
                throw new CommandException(409, "Format discovery can access the input hardware. Close every prepared or active capture before listing formats.");
            _inputHardwareProbeActive = true;
            monitors = RetireIdleMonitorsForProbeUnderGate();
            profile = Clone(_configuration.Capture);
        }
        finally { _gate.Release(); }
        try
        {
            await Task.WhenAll(monitors.Select(CloseMonitorAsync)).WaitAsync(TimeSpan.FromSeconds(55), token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await InputDiscoveryService.DiscoverFormatsAsync(profile, deviceName, linked.Token);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { _inputHardwareProbeActive = false; }
            finally { _gate.Release(); }
        }
    }
}
