namespace DeckCapture.Capture;

/// <summary>Resolves the optional paired media tools shipped with an application release.</summary>
public static class MediaToolPaths
{
    public static string Resolve(string configured) => Resolve(configured, AppContext.BaseDirectory, OperatingSystem.IsWindows());

    internal static string Resolve(string configured, string baseDirectory, bool windows)
    {
        // A user-specified path must always win. Only the default command names use
        // the release bundle, so installing another FFmpeg cannot change that bundle.
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var command = configured.Equals("ffmpeg", comparison) || (windows && configured.Equals("ffmpeg.exe", comparison)) ? "ffmpeg"
            : configured.Equals("ffprobe", comparison) || (windows && configured.Equals("ffprobe.exe", comparison)) ? "ffprobe" : null;
        if (command is null) return configured;
        var root = Path.GetFullPath(baseDirectory);
        var directories = new List<string> { Path.Combine(root, "media-tools") };
        // The worker sits exactly one directory below the release root. Do not
        // search arbitrary ancestors or the current working directory for executables.
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("capture-worker", comparison))
            directories.Add(Path.Combine(Directory.GetParent(Path.TrimEndingDirectorySeparator(root))!.FullName, "media-tools"));
        var suffix = windows ? ".exe" : "";
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            if (!File.Exists(Path.Combine(directory, "ffmpeg" + suffix)) || !File.Exists(Path.Combine(directory, "ffprobe" + suffix)))
                throw new InvalidDataException("The bundled media-tools directory must contain both FFmpeg and ffprobe for this platform.");
            return Path.Combine(directory, command + suffix);
        }
        return configured;
    }
}
