namespace DeckCapture.Delivery;

internal static class DeliveryPaths
{
    internal static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Root(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("Delivery roots must be absolute paths.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        NoLinks(full);
        return full;
    }

    internal static string Relative(string value, bool allowEmpty = false)
    {
        if (value.Length == 0 && allowEmpty) return "";
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new ArgumentException("A relative package path is required.");
        var parts = value.Split(['/', '\\']);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new ArgumentException("Unsafe package path.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Reserved Windows path name.");
        }
        return Path.Combine(parts);
    }

    internal static bool Contains(string root, string path) => string.Equals(root, path, Comparison) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);

    internal static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, path)) throw new ArgumentException("Path escapes its delivery root.");
        NoLinks(path);
        return path;
    }

    internal static void NoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Delivery paths cannot contain reparse points or symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void CreateDirectory(string path)
    {
        NoLinks(path);
        Directory.CreateDirectory(path);
        NoLinks(path);
    }
}
