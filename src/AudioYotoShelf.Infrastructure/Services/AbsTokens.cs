using System.Text.Json;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services;

/// <summary>
/// Shared Audiobookshelf access-token freshness logic, mirroring <see cref="YotoTokens"/>.
/// ABS v2.26+ issues short-lived JWT access tokens alongside a long-lived refresh token; this
/// re-mints (and persists) the access token when it is missing or within five minutes of expiry,
/// so a stored connection can be reused ad hoc without prompting the user for credentials again.
/// Legacy/opaque tokens (no embedded expiry, no refresh token) are returned unchanged.
/// </summary>
public static class AbsTokens
{
    /// <summary>
    /// Copies the tokens from an ABS login/refresh payload onto the stored connection, preferring
    /// the v2.26+ JWT access token and recording its expiry. Used by both the connect endpoint and
    /// <see cref="EnsureValidAsync"/> so token handling stays in one place.
    /// </summary>
    public static void ApplyLogin(UserConnection user, AbsUser absUser)
    {
        var accessToken = absUser.AccessToken ?? absUser.Token;
        user.AudiobookshelfToken = accessToken;
        user.AudiobookshelfTokenValidatedAt = DateTimeOffset.UtcNow;
        user.AudiobookshelfTokenExpiresAt = GetJwtExpiry(accessToken);

        // ABS rotates the refresh token on refresh; only overwrite when a new one is returned so a
        // cookie-less refresh that omits it doesn't wipe the stored token.
        if (!string.IsNullOrEmpty(absUser.RefreshToken))
            user.AudiobookshelfRefreshToken = absUser.RefreshToken;
    }

    /// <summary>
    /// Stores an Audiobookshelf API key as the connection's token. API keys carry no refresh token:
    /// any stored one (from an earlier password login) is dropped so <see cref="EnsureValidAsync"/>
    /// never replaces the key with a password-session token. An expired key needs reconnecting.
    /// </summary>
    public static void ApplyApiKey(UserConnection user, string apiKey)
    {
        user.AudiobookshelfToken = apiKey;
        user.AudiobookshelfRefreshToken = null;
        user.AudiobookshelfTokenValidatedAt = DateTimeOffset.UtcNow;
        user.AudiobookshelfTokenExpiresAt = GetJwtExpiry(apiKey);
    }

    public static async Task<string> EnsureValidAsync(
        AudioYotoShelfDbContext db, IAudiobookshelfService absService, UserConnection user,
        ILogger logger, CancellationToken ct)
    {
        // Nothing to do when there's no refresh token (legacy server) or the token isn't near expiry.
        if (string.IsNullOrEmpty(user.AudiobookshelfRefreshToken) ||
            user.AudiobookshelfTokenExpiresAt is null ||
            user.AudiobookshelfTokenExpiresAt.Value >= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return user.AudiobookshelfToken!;
        }

        logger.LogInformation("Refreshing Audiobookshelf token for user {Username} (expiry: {Expiry})",
            user.Username, user.AudiobookshelfTokenExpiresAt?.ToString() ?? "null");

        var login = await absService.RefreshTokenAsync(
            user.AudiobookshelfUrl, user.AudiobookshelfRefreshToken!, ct);
        ApplyLogin(user, login.User);
        await db.SaveChangesAsync(ct);
        return user.AudiobookshelfToken!;
    }

    /// <summary>
    /// Reads the <c>exp</c> claim from a JWT access token. Returns null for an opaque token that
    /// isn't a three-segment JWT, or when the claim is absent/unparseable — in which case the token
    /// is treated as non-expiring (legacy behavior).
    /// </summary>
    internal static DateTimeOffset? GetJwtExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            using var doc = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception)
        {
            // Not a parseable JWT payload — treat as non-expiring.
        }

        return null;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '='));
    }
}
