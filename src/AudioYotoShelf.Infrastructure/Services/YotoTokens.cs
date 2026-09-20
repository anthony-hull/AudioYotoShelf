using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services;

/// <summary>
/// Shared Yoto access-token freshness logic. Refreshes (and persists) the token when it is
/// missing or within five minutes of expiry. Centralized so the transfer orchestrators and
/// controllers don't each reimplement it.
/// </summary>
public static class YotoTokens
{
    public static async Task<string> EnsureValidAsync(
        AudioYotoShelfDbContext db, IYotoService yotoService, UserConnection user,
        ILogger logger, CancellationToken ct)
    {
        // Stryker disable once Equality : `>=` vs `>` differs only when the expiry equals the tick five minutes from now, which no test can pin
        if (user.YotoTokenExpiresAt.HasValue &&
            user.YotoTokenExpiresAt.Value >= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return user.YotoAccessToken!;
        }

        logger.LogInformation("Refreshing Yoto token for user {Username} (expiry: {Expiry})",
            user.Username, user.YotoTokenExpiresAt?.ToString() ?? "null");

        var newToken = await yotoService.RefreshTokenAsync(user.YotoRefreshToken!, ct);
        user.YotoAccessToken = newToken.AccessToken;
        user.YotoRefreshToken = newToken.RefreshToken ?? user.YotoRefreshToken;
        user.YotoTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(newToken.ExpiresIn);
        await db.SaveChangesAsync(ct);
        return newToken.AccessToken;
    }
}
