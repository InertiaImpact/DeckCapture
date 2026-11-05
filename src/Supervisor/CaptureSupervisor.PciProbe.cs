using DeckCapture.Capture;

namespace DeckCapture.Supervisor;

public sealed partial class CaptureSupervisor
{
    // Only this fixed diagnostic can bind DirectShow filters. Keep it separate from
    // ordinary discovery, which is allowed during recordings and must never bind them.
    internal Func<CancellationToken, Task<DeckLinkPciCorrelationResult>> PciIdentityProbeAsync { get; set; } =
        DeckLinkPciCorrelation.ProbeIdleAsync;

    public async Task<DeckLinkPciCorrelationResult> ProbePcieIdentityAsync(CancellationToken token)
    {
        IdleMonitor[] monitors;
        await _gate.WaitAsync(token);
        try
        {
            if (_shuttingDown || MaintenanceActive)
                throw new CommandException(409, "PCIe identity probing is unavailable during shutdown or maintenance.");
            EnsureInputDiscoveryIdleUnderGate();
            if (_opening.Count > 0 || _live.Count > 0 || _jobs.Values.Any(job => OwnsInputUnderGate(job)) )
                throw new CommandException(409, "PCIe identity probing may access capture filters. Close every prepared or active capture first.");
            _inputHardwareProbeActive = true;
            monitors = RetireIdleMonitorsForProbeUnderGate();
        }
        finally { _gate.Release(); }

        try
        {
            await Task.WhenAll(monitors.Select(CloseMonitorAsync)).WaitAsync(TimeSpan.FromSeconds(55), token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            return await PciIdentityProbeAsync(linked.Token);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try { _inputHardwareProbeActive = false; }
            finally { _gate.Release(); }
        }
    }
}
