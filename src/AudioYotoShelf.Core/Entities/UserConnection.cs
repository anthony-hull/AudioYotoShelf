namespace AudioYotoShelf.Core.Entities;

/// <summary>
/// Stores a user's connections to Audiobookshelf and Yoto.
/// ABS tokens are obtained via login delegation; Yoto tokens via OAuth authorization code flow.
/// Both connections persist a refresh token so the stored connection can be reused ad hoc
/// (re-minting a fresh access token in the background) without the user logging in again.
/// </summary>
public class UserConnection : BaseEntity
{
    public required string Username { get; set; }

    // Audiobookshelf connection
    public required string AudiobookshelfUrl { get; set; }
    public string? AudiobookshelfToken { get; set; }
    /// <summary>
    /// Refresh token from Audiobookshelf (v2.26+ JWT auth), used to silently mint a new
    /// access token when the stored one expires. Null for legacy/opaque-token servers.
    /// </summary>
    public string? AudiobookshelfRefreshToken { get; set; }
    /// <summary>
    /// Expiry of <see cref="AudiobookshelfToken"/>, parsed from the JWT's <c>exp</c> claim.
    /// Null when the token is a legacy opaque token with no embedded expiry.
    /// </summary>
    public DateTimeOffset? AudiobookshelfTokenExpiresAt { get; set; }
    public DateTimeOffset? AudiobookshelfTokenValidatedAt { get; set; }

    // Yoto OAuth connection
    public string? YotoAccessToken { get; set; }
    public string? YotoRefreshToken { get; set; }
    public DateTimeOffset? YotoTokenExpiresAt { get; set; }
    /// <summary>Stores OAuth state nonce during authorization code flow.</summary>
    public string? YotoDeviceCode { get; set; }

    // Preferences
    public string? DefaultLibraryId { get; set; }
    public int DefaultMinAge { get; set; } = 5;
    public int DefaultMaxAge { get; set; } = 10;

    // Admin & usage
    /// <summary>Grants access to the admin analytics area. Set in the DB, or bootstrapped at login
    /// for <c>Admin:Usernames</c> users who authenticate against the trusted
    /// <c>Admin:AudiobookshelfUrl</c> server. An admin session (role claim) is only minted when the
    /// login is against that trusted server, so this flag alone is not sufficient for access.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>Timestamp of the most recent successful ABS login (session start).</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    // Navigation
    public ICollection<CardTransfer> CardTransfers { get; set; } = [];
    public ICollection<GeneratedIcon> GeneratedIcons { get; set; } = [];
    public ICollection<LoginEvent> LoginEvents { get; set; } = [];

    public bool HasValidAbsConnection =>
        !string.IsNullOrEmpty(AudiobookshelfToken) &&
        AudiobookshelfTokenValidatedAt.HasValue &&
        // A legacy/opaque token has no known expiry (treated as non-expiring); a JWT access token
        // is valid while unexpired, or once expired so long as we hold a refresh token to renew it.
        (AudiobookshelfTokenExpiresAt is null ||
         AudiobookshelfTokenExpiresAt > DateTimeOffset.UtcNow ||
         !string.IsNullOrEmpty(AudiobookshelfRefreshToken));

    // Stryker disable once Equality : `>` vs `>=` differs only when the expiry equals the current tick, which no test can pin
    public bool HasValidYotoConnection =>
        !string.IsNullOrEmpty(YotoAccessToken) &&
        YotoTokenExpiresAt.HasValue &&
        YotoTokenExpiresAt > DateTimeOffset.UtcNow;
}
