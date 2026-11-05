using System.Text.Json.Serialization;

namespace DeckCapture.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState { Prepared, Waiting, Recording, Finalizing, Validating, Complete, NeedsReview, Failed, Interrupted, Canceled }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryState { Queued, Copying, Verifying, Delivered, Held, RetryScheduled, Failed }

public sealed record AppConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string TimeZone { get; init; } = "America/Chicago";
    public string CacheRoot { get; init; } = "";
    public long EmergencyReserveBytes { get; init; } = 1L << 30;
    public int[] DurationPresetsMinutes { get; init; } = [15, 30, 60, 90, 120];
    public int? DefaultDurationMinutes { get; init; }
    public bool ExperimentalEndDetection { get; init; }
    public Dictionary<string, MetadataField> MetadataFields { get; init; } = new()
    {
        ["station"] = new(true, false), ["operator"] = new(true, false),
        ["tapeId"] = new(false, false), ["barcode"] = new(false, false),
        ["batch"] = new(false, false), ["collection"] = new(false, false),
        ["title"] = new(false, false), ["description"] = new(false, false), ["notes"] = new(false, false)
    };
    public List<ChannelConfiguration> Channels { get; init; } = [];
    // Display-only operator assertions; these never rewrite a channel's exact capture device key.
    public List<PhysicalInputWiring> InputWiring { get; init; } = [];
    public List<DestinationConfiguration> Destinations { get; init; } = [];
    public CaptureProfile Capture { get; init; } = new();
    public DetectionSettings Detection { get; init; } = new();
    public TransferSettings Transfer { get; init; } = new();
    public NotificationSettings Notifications { get; init; } = new();
}
public sealed record PhysicalInputWiring
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string ModelId { get; init; } = "";
    public List<PhysicalInputBinding> Inputs { get; init; } = [];
    public List<PhysicalInputConnector> CustomConnectors { get; init; } = [];
}
public sealed record PhysicalInputBinding(string DeviceName, string ConnectorId);
public sealed record PhysicalInputConnector(string Id, string Label, string Kind, string Direction);
public sealed record MetadataField(bool Visible, bool Required);
public sealed record ChannelConfiguration
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Deck { get; init; } = "";
    public string SourceKind { get; init; } = "Synthetic";
    public string DeviceName { get; init; } = "";
    public string FormatCode { get; init; } = "auto";
    public string SyntheticPattern { get; init; } = "testsrc2";
    public int AudioChannels { get; init; } = 2;
}
public sealed record DestinationConfiguration(string Id, string Label, string Root);
public static class CaptureFormatCode
{
    public static bool IsAutomatic(string? value) => string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
    public static bool IsValid(string? value) => IsAutomatic(value) || (!string.IsNullOrWhiteSpace(value) && value.Length is 3 or 4 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == ' '));
}
public sealed record CaptureProfile
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";
    public int PreRollSeconds { get; init; } = 5;
    public int SyntheticWidth { get; init; } = 640;
    public int SyntheticHeight { get; init; } = 360;
    public int SyntheticFramesPerSecond { get; init; } = 25;
    public int MaximumArmedMinutes { get; init; } = 15;
    public int MonitoringTimeoutMinutes { get; init; }
    public long ReservationBytesPerSecond { get; init; } = 30_000_000;
    public bool OfflineQcEnabled { get; init; }
    public int EncoderThreads { get; init; } = 2;
    public bool LivePreviewEnabled { get; init; } = true;
}
public sealed record DetectionSettings
{
    public string DefaultEndMode { get; init; } = "Alert";
    public int MinimumRecordedSeconds { get; init; } = 300;
    public int BlackSeconds { get; init; } = 120;
    public double BlackRatio { get; init; } = .99;
    public double SilenceDbfs { get; init; } = -50;
    public int GraceSeconds { get; init; } = 30;
    public int StartConfirmationSeconds { get; init; } = 2;
}
public sealed record TransferSettings
{
    public bool ScheduleEnabled { get; init; } = true;
    public List<TransferWindow> Windows { get; init; } = [new()];
    public int MaximumConcurrent { get; init; } = 2;
    public double PerTransferMiBps { get; init; } = 20;
    public double OutsideWindowMiBps { get; init; } = 2;
    public double AggregateMiBps { get; init; } = 40;
}
public sealed record TransferWindow
{
    public DayOfWeek[] StartDays { get; init; } = Enum.GetValues<DayOfWeek>();
    public string StartLocal { get; init; } = "22:00";
    public string EndLocal { get; init; } = "06:00";
}
public sealed record NotificationSettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 25;
    public string TlsMode { get; init; } = "Relay";
    public string Sender { get; init; } = "";
    public string[] Recipients { get; init; } = [];
    public string Username { get; init; } = "";
    // Missing/null retains the pre-existing username-based authentication behavior.
    // New settings clients send an explicit choice, so a saved username need not enable login.
    public bool? AuthenticationEnabled { get; init; }
    [JsonIgnore] public bool UsesAuthentication => AuthenticationEnabled ?? !string.IsNullOrEmpty(Username);
    // Retained for old configuration round trips; the password is stored separately.
    public string CredentialReference { get; init; } = "";
    public bool CaptureGroupComplete { get; init; } = true;
    public bool TransferWindowClosed { get; init; } = true;
    public string? DailyDigestLocalTime { get; init; }
}
public sealed record PrepareRecordingRequest(string ChannelId, string FileName, int DurationSeconds, string DestinationId,
    string RelativeDirectory, Dictionary<string, string>? Metadata = null, bool WaitForContent = false, string EndMode = "Off", string? BatchId = null, bool StartImmediately = false);
public sealed record RecordingJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ChannelId { get; init; } = "";
    public string FileName { get; init; } = "";
    public int DurationSeconds { get; set; }
    public int OriginalDurationSeconds { get; init; }
    public string DestinationId { get; init; } = "";
    public string DestinationRoot { get; init; } = "";
    public string RelativeDirectory { get; init; } = "";
    public string PackageDirectory { get; init; } = "";
    public string? BatchId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public AppConfiguration Configuration { get; init; } = new();
    public ChannelConfiguration Channel { get; init; } = new();
    public JobState State { get; set; } = JobState.Prepared;
    public long Revision { get; set; } = 1;
    public DateTimeOffset PreparedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double RecordedSeconds { get; set; }
    public QcProgressSnapshot? QcProgress { get; set; }
    public string EndMode { get; init; } = "Off";
    public bool ExperimentalEndDetectionActive { get; set; }
    public string? StopReason { get; set; }
    public string? Error { get; set; }
    public long ReservedBytes { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<ArtifactFile> Artifacts { get; set; } = [];
}
// Observed background work only; persisted progress never establishes a QC pass.
public sealed record QcProgressSnapshot(string Phase, long Frames, double ProcessedSeconds,
    double? DurationSeconds, DateTimeOffset UpdatedAt);
public sealed record ArtifactFile(string RelativePath, long Size, string Sha256);
public sealed record CaptureCapabilities(bool Available, bool DeckLinkSupported, string Version, string? Error = null);
public sealed record CaptureMetrics
{
    // Null is unknown/unavailable. Packet flow or black pixels do not establish hardware lock.
    public bool? SignalLocked { get; init; }
    public bool? InputSignalPresent { get; init; }
    public string InputSignalStatusSource { get; init; } = "Unavailable";
    public bool AnalysisAvailable { get; init; }
    public double? AnalysisAgeSeconds { get; init; }
    public bool InputBufferOverrun { get; init; }
    public bool OutputFrameDuplication { get; init; }
    public string? IntegrityWarning { get; init; }
    public bool IsBlack { get; init; }
    public double BlackRatio { get; init; }
    public double AudioRmsDbfs { get; init; } = -100;
    public double[] AudioChannelRmsDbfs { get; init; } = [];
    public long DroppedFrames { get; init; }
    public long Frames { get; init; }
    public double MediaSeconds { get; init; }
    public long? RecordingFrames { get; init; }
    public double? RecordingMediaSeconds { get; init; }
    public string? DetectedVideoFormat { get; init; }
    public string? PreviewPath { get; init; }
    public string? LivePreviewPath { get; init; }
    public string? LivePreviewUrl { get; init; }
    public bool LivePreviewAvailable { get; init; }
    public string? LivePreviewError { get; init; }
    public double[] LumaWaveform { get; init; } = [];
    public string? Warning { get; init; }
}
public sealed record CaptureOpenOptions(ChannelConfiguration Channel, CaptureProfile Profile, string WorkingDirectory)
{
    // Explicit session intent; never infer it from absent recording metadata or pre-roll.
    public bool MonitorOnly { get; init; }
    // Supervisor-to-worker preflight outcome; not an operator-configured encoder or public URL.
    public bool LivePreviewPreflightCompleted { get; init; }
    public string? LivePreviewEncoderName { get; init; }
    public string? LivePreviewPreflightError { get; init; }
    public string[] LivePreviewPreflightDiagnostics { get; init; } = [];
}
public sealed record CaptureResult(bool Success, string? MediaPath, double DurationSeconds, long Frames, long DroppedFrames, string? Error = null);
public interface ICaptureBackend
{
    Task<CaptureCapabilities> ProbeAsync(CaptureProfile profile, CancellationToken cancellationToken);
    Task<ICaptureSession> OpenAsync(CaptureOpenOptions options, CancellationToken cancellationToken);
}
public interface ICaptureSession : IAsyncDisposable
{
    CaptureMetrics Metrics { get; }
    Task<CaptureResult> Completion { get; }
    Task BeginRecordingAsync(string outputPath, int durationSeconds, int preRollSeconds, CancellationToken cancellationToken);
    Task UpdateDurationAsync(int totalDurationSeconds, CancellationToken cancellationToken);
    Task<CaptureResult> StopAsync(CancellationToken cancellationToken);
}
public sealed record DeliveryJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string RecordingId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SourceDirectory { get; init; } = "";
    public string DestinationRoot { get; init; } = "";
    public string RelativeDirectory { get; init; } = "";
    public List<ArtifactFile> Files { get; init; } = [];
    public DeliveryState State { get; set; } = DeliveryState.Queued;
    public bool Override { get; set; }
    public long BytesCopied { get; set; }
    public long TotalBytes { get; set; }
    public double RateMiBps { get; set; }
    public string? DestinationPath { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
public sealed record DeliveryTransferRequest(string JobId, string SourceDirectory, string DestinationRoot, string RelativeDirectory,
    string CheckpointDirectory, IReadOnlyList<ArtifactFile> Files);
public sealed record DeliveryRates(double PerTransferMiBps, double AggregateMiBps);
public sealed record DeliveryProgress(string Phase, long BytesCopied, long TotalBytes, double RateMiBps);
public sealed record DeliveryResult(string DestinationPath, bool Verified);
public interface IFileDelivery
{
    Task<DeliveryResult> TransferAsync(DeliveryTransferRequest request, Func<DeliveryRates> rates,
        IProgress<DeliveryProgress>? progress, CancellationToken cancellationToken);
}
