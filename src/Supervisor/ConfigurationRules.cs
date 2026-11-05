using DeckCapture.Domain;

namespace DeckCapture.Supervisor;

public static class ConfigurationRules
{
    public static AppConfiguration DevelopmentDefaults(string directory) => new()
    {
        CacheRoot = Path.Combine(directory, "media"),
        Channels = Enumerable.Range(1, 4).Select(i => new ChannelConfiguration { Id = $"ch{i}", Label = $"Channel {i:00}", Deck = "Synthetic test input", DeviceName = $"synthetic-{i}" }).ToList(),
        Destinations = [new("local-test", "Local delivery test", Path.Combine(directory, "delivered"))]
    };
    public static List<string> Validate(AppConfiguration? config)
    {
        var errors = new List<string>();
        if (config is null) return ["Configuration is required."];
        if (config.SchemaVersion != 1) errors.Add("Unsupported schemaVersion. Expected 1.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone); } catch { errors.Add("Unknown time zone."); }
        var validCacheRoot = AbsolutePath(config.CacheRoot);
        if (!validCacheRoot) errors.Add("Cache root must be an absolute local path.");
        if (config.CacheRoot?.StartsWith(@"\\", StringComparison.Ordinal) == true) errors.Add("Capture cache must be local, not a UNC share.");
        else if (validCacheRoot && IsKnownNetworkDrive(config.CacheRoot!)) errors.Add("Capture cache must be local, not a mapped network drive.");
        if (config.EmergencyReserveBytes < 0) errors.Add("Emergency reserve cannot be negative.");
        InputWiringRules.Validate(config.InputWiring, errors);
        if (config.Channels is null || config.Channels.Count is < 1 or > 16 || config.Channels.Any(c => c is null)) errors.Add("Configure between 1 and 16 non-null channels.");
        else
        {
            if (config.Channels.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Channels.Count) errors.Add("Channel IDs must be unique.");
            foreach (var c in config.Channels)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 40 || !c.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) errors.Add("Channel IDs may contain only letters, digits, hyphens and underscores.");
                if (c.SourceKind is not ("Synthetic" or "DeckLink")) errors.Add($"Unsupported source kind for {c.Id}.");
                if (c.SourceKind == "DeckLink" && string.IsNullOrWhiteSpace(c.DeviceName)) errors.Add($"DeckLink device name is required for {c.Id}.");
                if (c.SourceKind == "DeckLink" && !CaptureFormatCode.IsValid(c.FormatCode)) errors.Add($"DeckLink format for {c.Id} must be auto or a three/four-character format code.");
                if (c.SyntheticPattern is not ("testsrc2" or "black")) errors.Add("Synthetic pattern must be testsrc2 or black.");
                if (c.AudioChannels is not (2 or 8 or 16)) errors.Add("Audio channel count must be 2, 8 or 16.");
            }
            if (config.Channels.Where(c => c.SourceKind == "DeckLink").GroupBy(c => c.DeviceName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) errors.Add("A DeckLink input may be assigned to only one channel.");
        }
        if (config.Destinations is null || config.Destinations.Count == 0 || config.Destinations.Any(d => d is null)) errors.Add("At least one destination is required; destination entries cannot be null.");
        else
        {
            if (config.Destinations.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Destinations.Count) errors.Add("Destination IDs must be unique.");
            foreach (var d in config.Destinations)
            {
                if (string.IsNullOrWhiteSpace(d.Id) || !AbsolutePath(d.Root)) errors.Add("Each destination needs an ID and an absolute root.");
                else if (validCacheRoot && PathSafety.Overlaps(config.CacheRoot, d.Root)) errors.Add("Capture cache and delivery roots must not overlap.");
            }
        }
        if (config.MetadataFields is null || config.MetadataFields.Any(f => !MetadataKeys.Contains(f.Key) || f.Value is null || (!f.Value.Visible && f.Value.Required))) errors.Add("Unknown metadata fields or a hidden field marked required.");
        if (config.DurationPresetsMinutes is null || config.DurationPresetsMinutes.Length > 12 || config.DurationPresetsMinutes.Any(d => d is < 1 or > 720)) errors.Add("Duration presets must be between 1 and 720 minutes.");
        if (config.DefaultDurationMinutes is { } defaultDuration && config.DurationPresetsMinutes?.Contains(defaultDuration) != true)
            errors.Add("Default duration must be one of the duration presets.");
        var capture = config.Capture;
        if (capture is not null && capture.EncoderThreads is < 1 or > 64) errors.Add("Capture encoder threads must be between 1 and 64 per input.");
        if (capture is not null && capture.MonitoringTimeoutMinutes is < 0 or > 1440) errors.Add("Monitoring timeout must be between 0 and 1440 minutes; 0 means no timeout.");
        if (capture is null || capture.PreRollSeconds is < 0 or > 30 || capture.ReservationBytesPerSecond is < 100_000 or > 1_000_000_000 || capture.MaximumArmedMinutes is < 1 or > 120 || capture.SyntheticWidth is < 64 or > 1920 || capture.SyntheticHeight is < 64 or > 1080 || capture.SyntheticFramesPerSecond is < 1 or > 60 || string.IsNullOrWhiteSpace(capture.FfmpegPath) || string.IsNullOrWhiteSpace(capture.FfprobePath)) errors.Add("Invalid capture profile.");
        var detection = config.Detection;
        if (detection is null || detection.DefaultEndMode is not ("Off" or "Alert" or "Stop") || detection.MinimumRecordedSeconds < 0 || detection.BlackSeconds is < 1 or > 3600 || detection.GraceSeconds is < 1 or > 300 || detection.StartConfirmationSeconds is < 1 or > 30 || !double.IsFinite(detection.BlackRatio) || detection.BlackRatio is < .5 or > 1 || !double.IsFinite(detection.SilenceDbfs) || detection.SilenceDbfs is < -100 or > 0) errors.Add("Invalid detection profile.");
        var transfer = config.Transfer;
        if (transfer is null || transfer.MaximumConcurrent is < 1 or > 16 || !PositiveRate(transfer.PerTransferMiBps) || !PositiveRate(transfer.OutsideWindowMiBps) || !PositiveRate(transfer.AggregateMiBps)) errors.Add("Transfer concurrency and finite positive speeds are required.");
        else if (transfer.Windows is null || transfer.Windows.Count > 30 || transfer.Windows.Any(w => w is null || w.StartDays is null || w.StartDays.Length == 0 || w.StartDays.Any(d => !Enum.IsDefined(d)) || !TimeOnly.TryParseExact(w.StartLocal, "HH:mm", out var start) || !TimeOnly.TryParseExact(w.EndLocal, "HH:mm", out var end) || start == end)) errors.Add("Transfer windows need non-null entries, valid weekdays and different HH:mm start/end times.");
        var notification = config.Notifications;
        if (notification is null || notification.Port is < 1 or > 65535 || notification.TlsMode is not ("StartTls" or "Tls" or "Relay") || notification.Recipients is null || notification.Recipients.Any(string.IsNullOrWhiteSpace)) errors.Add("Invalid SMTP settings; recipients cannot contain null or blank entries.");
        else if (notification.Enabled && (string.IsNullOrWhiteSpace(notification.Host) || string.IsNullOrWhiteSpace(notification.Sender) || notification.Recipients is null || notification.Recipients.Length == 0)) errors.Add("Enabled SMTP needs a host, sender, and recipients.");
        if (notification is { Enabled: true, AuthenticationEnabled: true })
        {
            if (string.IsNullOrWhiteSpace(notification.Username)) errors.Add("SMTP login needs a username.");
        }
        if (notification?.DailyDigestLocalTime is { } daily && !TimeOnly.TryParseExact(daily, "HH:mm", out _)) errors.Add("Daily digest time must use HH:mm.");
        return errors;
    }
    public static readonly HashSet<string> MetadataKeys = ["station", "operator", "tapeId", "barcode", "batch", "collection", "title", "description", "notes"];
    private static bool PositiveRate(double value) => double.IsFinite(value) && value > 0 && value <= 10_000;
    private static bool AbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try { _ = Path.GetFullPath(value); return true; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool IsKnownNetworkDrive(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveType == DriveType.Network; }
        // Configuration may reference a currently unavailable local volume. Do
        // not invent a network classification when Windows cannot identify it.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}

public static class PathSafety
{
    public static bool Overlaps(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) || !Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
    public static string UnderRoot(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root)) throw new CommandException(400, "Use an absolute approved destination root.");
        if (Path.IsPathRooted(relative)) throw new CommandException(400, "Use a relative destination folder.");
        if (relative.Length != 0)
        {
            var parts = relative.Split(['/', '\\']);
            foreach (var part in parts) ValidatePathSegment(part);
            relative = Path.Combine(parts);
        }
        // Keep a volume root's separator: C: is drive-relative, unlike C:\.
        var cleanRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(cleanRoot, relative));
        var prefix = Path.EndsInDirectorySeparator(cleanRoot) ? cleanRoot : cleanRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(path, cleanRoot, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new CommandException(400, "Destination escapes its approved root.");
        // Checking only the configured root misses a root inside a junction.
        // ACLs must prevent concurrent replacement after these path checks.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new CommandException(400, "Reparse points are not allowed in an approved destination path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
    public static void ValidateFileName(string fileName)
    {
        ValidatePathSegment(fileName);
        if (fileName.Length > 100) throw new CommandException(400, "File names must contain at most 100 characters.");
    }
    private static void ValidatePathSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) >= 0 || name.Any(char.IsControl) || System.Text.RegularExpressions.Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new CommandException(400, "Choose a safe Windows name without reserved characters or device names.");
    }
}
public sealed class CommandException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
