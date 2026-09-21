namespace AudioYotoShelf.Core.DTOs.Audiobookshelf;

/// <summary>
/// The result of asking Audiobookshelf to begin a single sign-on flow on someone's behalf.
/// </summary>
/// <param name="AuthorizationUrl">Where to send the person's browser: the identity provider's sign-in.</param>
/// <param name="Cookies">
/// The session cookies Audiobookshelf set, as name → value. Audiobookshelf refuses the final
/// code exchange ("No session") without them, so they must be kept until it happens.
/// </param>
/// <param name="State">Ties the identity provider's redirect back to this attempt.</param>
/// <param name="CodeVerifier">The PKCE secret whose S256 hash Audiobookshelf already holds as the challenge.</param>
public record AbsSsoStart(
    string AuthorizationUrl,
    IReadOnlyDictionary<string, string> Cookies,
    string State,
    string CodeVerifier);
