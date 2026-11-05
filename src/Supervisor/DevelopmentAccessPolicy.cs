using System.Net;

namespace DeckCapture.Supervisor;

/// <summary>Validates listener admission. Configured-token authorization remains the request middleware's responsibility.</summary>
public static class DevelopmentAccessPolicy
{
    public static bool ValidateBinding(string urls, string? token, bool allowDevelopmentHttp)
    {
        if (string.IsNullOrWhiteSpace(urls) || urls.Any(char.IsControl))
            throw new InvalidOperationException("Provide one or more valid HTTP or HTTPS listener URLs.");
        var remoteBinding = false;
        var allHttps = true;
        foreach (var item in urls.Split(';'))
        {
            var endpoint = item.Trim();
            // Kestrel accepts * and + wildcard listeners; normalize only that authority
            // for URI validation while retaining its remote classification.
            var normalized = endpoint;
            var wildcard = false;
            var schemeEnd = endpoint.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd > 0)
            {
                var authorityStart = schemeEnd + 3;
                var authorityEnd = endpoint.IndexOfAny(['/', '?', '#'], authorityStart);
                var authority = endpoint[authorityStart..(authorityEnd < 0 ? endpoint.Length : authorityEnd)];
                if (authority.Length > 0 && authority[0] is '*' or '+' && (authority.Length == 1 || authority[1] == ':'))
                {
                    normalized = endpoint[..authorityStart] + "0.0.0.0" + endpoint[(authorityStart + 1)..];
                    wildcard = true;
                }
            }
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0
                || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.HostNameType == UriHostNameType.Unknown)
                throw new InvalidOperationException("Listener URLs must be absolute HTTP or HTTPS addresses without credentials, paths, queries or fragments.");
            remoteBinding |= wildcard || !IsLocalHost(uri.IdnHost);
            allHttps &= uri.Scheme == Uri.UriSchemeHttps;
        }
        if (remoteBinding && !allowDevelopmentHttp && (!allHttps || string.IsNullOrWhiteSpace(token) || token.Length < 24))
            throw new InvalidOperationException("LAN access requires HTTPS and a DECKCAPTURE_ACCESS_TOKEN of at least 24 characters. Default binding is loopback; DECKCAPTURE_DEVELOPMENT_HTTP=1 explicitly enables development HTTP access.");
        return remoteBinding;
    }

    public static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }
}
