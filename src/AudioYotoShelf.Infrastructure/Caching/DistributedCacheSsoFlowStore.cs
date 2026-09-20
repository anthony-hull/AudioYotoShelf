using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioYotoShelf.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace AudioYotoShelf.Infrastructure.Caching;

/// <summary>
/// Holds single sign-on attempts in flight in the distributed cache (Redis) — throwaway state that
/// does not belong in Postgres. Deliberately does not swallow cache failures the way
/// <see cref="RedisCacheService"/> does: a flow that could not be saved cannot be completed, and
/// the person is better told so than sent round a loop that ends in "expired".
/// </summary>
public class DistributedCacheSsoFlowStore(IDistributedCache cache) : ISsoFlowStore
{
    private const string KeyPrefix = "abs-sso:";

    public async Task SaveAsync(string state, PendingSsoFlow flow, TimeSpan lifetime, CancellationToken ct = default)
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime };
        await cache.SetStringAsync(KeyPrefix + state, JsonSerializer.Serialize(flow), options, ct);
    }

    public async Task<PendingSsoFlow?> TakeAsync(string state, string? browserNonce, CancellationToken ct = default)
    {
        var key = KeyPrefix + state;
        var json = await cache.GetStringAsync(key, ct);
        if (json is null)
            return null;

        // Used up whether or not the caller is the right browser: a state that has been presented
        // by the wrong one is not to be trusted again. IDistributedCache has no atomic take, so two
        // simultaneous callbacks could both read it; Audiobookshelf's own one-use code is the backstop.
        await cache.RemoveAsync(key, ct);

        var flow = JsonSerializer.Deserialize<PendingSsoFlow>(json);
        return flow is not null && IsSameNonce(flow.BrowserNonce, browserNonce) ? flow : null;
    }

    private static bool IsSameNonce(string expected, string? presented) =>
        !string.IsNullOrEmpty(presented) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
