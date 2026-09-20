using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services;

/// <summary>
/// Keeps stored connections on the configured Audiobookshelf server.
/// </summary>
public static class AbsServerLock
{
    /// <summary>
    /// Repoints any connection that names a different server, and returns how many moved. The
    /// connect endpoint pins new connections to the configured server, but every later request
    /// reads the URL stored on the connection — so rows written before the server was configured
    /// would go on being fetched from their old server. Run at startup, after migrations.
    /// </summary>
    public static async Task<int> RepointConnectionsAsync(
        AudioYotoShelfDbContext db, string configuredUrl, ILogger logger, CancellationToken ct = default)
    {
        var stale = await db.UserConnections
            .Where(u => u.AudiobookshelfUrl != configuredUrl)
            .ToListAsync(ct);

        // The provider-side comparison above is case- and slash-sensitive; equivalent URLs are not stale.
        stale = [.. stale.Where(u => !AudiobookshelfServer.IsSameServer(u.AudiobookshelfUrl, configuredUrl))];
        if (stale.Count == 0)
            return 0;

        foreach (var connection in stale)
            connection.AudiobookshelfUrl = configuredUrl;

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Repointed {Count} Audiobookshelf connection(s) to the configured server {ConfiguredUrl}",
            stale.Count, configuredUrl);

        return stale.Count;
    }
}
