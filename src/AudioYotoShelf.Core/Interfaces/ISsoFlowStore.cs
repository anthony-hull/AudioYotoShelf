namespace AudioYotoShelf.Core.Interfaces;

/// <summary>What must survive between sending someone to sign in and their coming back.</summary>
/// <param name="CodeVerifier">The PKCE secret for the code exchange.</param>
/// <param name="AbsCookies">Audiobookshelf's session cookies from the start of the flow.</param>
/// <param name="BrowserNonce">Random value also held by the browser that started the flow, so no other browser can finish it.</param>
public record PendingSsoFlow(
    string CodeVerifier,
    IReadOnlyDictionary<string, string> AbsCookies,
    string BrowserNonce);

/// <summary>
/// Short-lived, throwaway storage for single sign-on attempts in flight. Entries are single-use:
/// whoever takes one, right or wrong, uses it up.
/// </summary>
public interface ISsoFlowStore
{
    Task SaveAsync(string state, PendingSsoFlow flow, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Returns the flow saved under <paramref name="state"/> and removes it. Null when there is none,
    /// it expired, it was already taken, or <paramref name="browserNonce"/> is not the one it was saved with.
    /// </summary>
    Task<PendingSsoFlow?> TakeAsync(string state, string? browserNonce, CancellationToken ct = default);
}
