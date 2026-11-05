using System.Text.Json;
using DeckCapture.Domain;
using DeckCapture.Supervisor;

namespace DeckCapture.Tests;

public sealed partial class SupervisorTests
{
    [Fact]
    public async Task OneAcceptedPrepareCanStartRecordingDespiteRequestDisconnectDuringOpen()
    {
        await using var fixture = new Fixture(); fixture.Backend.BlockedChannel = "ch1";
        using var request = new CancellationTokenSource();
        var command = fixture.Supervisor.PrepareAsync(fixture.Request() with { StartImmediately = true }, request.Token);
        await fixture.Backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        request.Cancel(); fixture.Backend.ReleaseOpen.TrySetResult();
        var recording = await command.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JobState.Recording, recording.State);
        Assert.Equal(1, fixture.Backend.OpenCalls); Assert.Equal(1, fixture.Backend.Sessions["ch1"].BeginCalls);
        Assert.False(fixture.Backend.Sessions["ch1"].RecordingToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ImmediateStartAndContentWaitAreRejectedBeforeAcceptingAnyJob()
    {
        await using var fixture = new Fixture();
        var failure = await Assert.ThrowsAsync<CommandException>(() => fixture.Supervisor.PrepareAsync(fixture.Request() with { StartImmediately = true, WaitForContent = true }, CancellationToken.None));
        Assert.Equal(400, failure.StatusCode); Assert.Equal(0, fixture.Backend.OpenCalls);
        Assert.Empty(fixture.Store.ReadAll<RecordingJob>("recording"));
        Assert.False(fixture.Request().StartImmediately);
    }

    [Fact]
    public async Task DefaultDurationRetainsLegacyPresetOrderAndRoundTripsExplicitSelection()
    {
        await using var fixture = new Fixture();
        var legacy = JsonSerializer.Deserialize<AppConfiguration>("{\"durationPresetsMinutes\":[7,13]}", StateStore.Json)!;
        Assert.Null(legacy.DefaultDurationMinutes); Assert.Equal(7, legacy.DurationPresetsMinutes[0]);
        var config = await fixture.Supervisor.ConfigurationAsync();
        await fixture.Supervisor.UpdateConfigurationAsync(config with { DurationPresetsMinutes = [7, 13], DefaultDurationMinutes = 13 }, CancellationToken.None);
        await fixture.RestartAsync();
        config = await fixture.Supervisor.ConfigurationAsync();
        Assert.Equal(13, config.DefaultDurationMinutes); Assert.Equal(new[] { 7, 13 }, config.DurationPresetsMinutes);
        Assert.Equal(13, JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(config, StateStore.Json), StateStore.Json)!.DefaultDurationMinutes);
        var errors = ConfigurationRules.Validate(config with { DefaultDurationMinutes = 15 });
        Assert.Contains(errors, error => error.Contains("Default duration", StringComparison.Ordinal));
        Assert.DoesNotContain(ConfigurationRules.Validate(config with { DefaultDurationMinutes = null }), error => error.Contains("Default duration", StringComparison.Ordinal));
    }
}
