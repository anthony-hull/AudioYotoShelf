namespace AudioYotoShelf.Core.Configuration;

/// <summary>
/// The single Audiobookshelf server this deployment talks to, when one is configured. Optional:
/// with no value set, users give their own server URL on the setup screen.
/// </summary>
public static class AudiobookshelfServer
{
    public const string UrlConfigKey = "Audiobookshelf:Url";

    /// <summary>
    /// Normalises the configured URL, returning null when it is unset. Resolved once at startup so
    /// a malformed value fails the boot rather than every connect attempt with a generic 500.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not an absolute HTTP(S) URL.</exception>
    public static string? ResolveConfiguredUrl(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return null;

        var trimmed = configuredValue.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{UrlConfigKey} must be an absolute HTTP or HTTPS URL (for example https://abs.example.com), " +
                $"but was \"{trimmed}\".");
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Whether two Audiobookshelf URLs address the same server.</summary>
    public static bool IsSameServer(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
