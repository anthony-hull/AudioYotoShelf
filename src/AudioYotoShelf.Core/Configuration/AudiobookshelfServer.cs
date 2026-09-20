namespace AudioYotoShelf.Core.Configuration;

/// <summary>
/// The single Audiobookshelf server this deployment talks to, when one is configured. Optional:
/// with no value set, users give their own server URL on the setup screen.
/// </summary>
public static class AudiobookshelfServer
{
    public const string UrlConfigKey = "Audiobookshelf:Url";

    /// <summary>
    /// The address browsers reach Audiobookshelf at, when <see cref="UrlConfigKey"/> is an internal
    /// name. Single sign-on needs it: Audiobookshelf builds the URL the identity provider sends the
    /// browser back to from the host it was called on, and a container name is no use to a browser.
    /// </summary>
    public const string PublicUrlConfigKey = "Audiobookshelf:PublicUrl";

    /// <summary>
    /// Normalises the configured URL, returning null when it is unset. Resolved once at startup so
    /// a malformed value fails the boot rather than every connect attempt with a generic 500.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not an absolute HTTP(S) URL.</exception>
    public static string? ResolveConfiguredUrl(string? configuredValue, string configKey = UrlConfigKey)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return null;

        var trimmed = configuredValue.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{configKey} must be an absolute HTTP or HTTPS URL (for example https://abs.example.com), " +
                $"but was \"{trimmed}\".");
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Whether two Audiobookshelf URLs address the same server.</summary>
    public static bool IsSameServer(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
