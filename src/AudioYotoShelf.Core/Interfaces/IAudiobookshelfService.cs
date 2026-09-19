using AudioYotoShelf.Core.DTOs.Audiobookshelf;

namespace AudioYotoShelf.Core.Interfaces;

/// <summary>
/// Handles all communication with the Audiobookshelf API.
/// Each method requires the user's ABS token for per-user permission scoping.
/// </summary>
public interface IAudiobookshelfService
{
    // Auth
    Task<AbsLoginResponse> LoginAsync(string baseUrl, string username, string password, CancellationToken ct = default);
    Task<bool> ValidateTokenAsync(string baseUrl, string token, CancellationToken ct = default);

    /// <summary>
    /// Resolves the user an Audiobookshelf API key (v2.26+, Settings → API Keys) acts as.
    /// Lets accounts that sign in to ABS through OpenID Connect, and so have no password, connect.
    /// Returns the same shape as <see cref="LoginAsync"/>, minus any tokens: the key itself is the token.
    /// </summary>
    Task<AbsLoginResponse> AuthorizeApiKeyAsync(string baseUrl, string apiKey, CancellationToken ct = default);

    /// <summary>
    /// Exchanges a stored Audiobookshelf refresh token (v2.26+ JWT auth) for a fresh access token,
    /// letting a stored connection be reused without re-prompting the user for credentials.
    /// Returns the same shape as <see cref="LoginAsync"/>.
    /// </summary>
    Task<AbsLoginResponse> RefreshTokenAsync(string baseUrl, string refreshToken, CancellationToken ct = default);

    // Libraries
    Task<AbsLibrary[]> GetLibrariesAsync(string baseUrl, string token, CancellationToken ct = default);
    Task<AbsLibraryItemsResponse> GetLibraryItemsAsync(string baseUrl, string token, string libraryId, int page = 0, int limit = 20, string? sort = null, bool desc = false, bool collapseSeries = false, string? search = null, string? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Full-text search within a library via the dedicated ABS search endpoint
    /// (the items endpoint does not support free-text search). Returns matched book items.
    /// </summary>
    Task<AbsLibraryItem[]> SearchLibraryItemsAsync(string baseUrl, string token, string libraryId, string query, int limit = 20, CancellationToken ct = default);

    // Books
    Task<AbsLibraryItem> GetLibraryItemAsync(string baseUrl, string token, string itemId, CancellationToken ct = default);
    Task<Stream> GetCoverImageAsync(string baseUrl, string token, string itemId, CancellationToken ct = default);

    // Series
    Task<AbsSeriesResponse> GetSeriesAsync(string baseUrl, string token, string libraryId, int page = 0, int limit = 20, CancellationToken ct = default);
    Task<AbsSeriesItem> GetSeriesDetailAsync(string baseUrl, string token, string libraryId, string seriesId, CancellationToken ct = default);

    // File download (streams to avoid memory buffering)
    Task<Stream> DownloadAudioFileAsync(string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default);
    Task<(Stream Stream, long ContentLength, string ContentType)> DownloadAudioFileWithMetadataAsync(string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default);
}
