using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Interfaces;

namespace AudioYotoShelf.IntegrationTests;

/// <summary>
/// Stands in for the external Audiobookshelf API so the integration tests exercise the real HTTP
/// pipeline, auth/session, EF/Postgres, and admin logic without reaching a live ABS server.
/// Only the members the tested endpoints touch are implemented.
/// </summary>
public sealed class FakeAudiobookshelfService : IAudiobookshelfService
{
    /// <summary>Username the fake ABS server reports for the next login.</summary>
    public string Username { get; set; } = "alice";

    /// <summary>Base URL of the most recent login or API-key authorize call.</summary>
    public string? LastBaseUrl { get; private set; }

    public Task<AbsLoginResponse> LoginAsync(string baseUrl, string username, string password, CancellationToken ct = default)
    {
        LastBaseUrl = baseUrl;
        return Task.FromResult(CurrentUserResponse());
    }

    public Task<AbsLoginResponse> AuthorizeApiKeyAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        LastBaseUrl = baseUrl;
        return Task.FromResult(CurrentUserResponse());
    }

    private readonly Dictionary<string, AbsSsoStart> _ssoStartsByState = [];

    public Task<AbsSsoStart> StartSsoAsync(string baseUrl, string? publicBaseUrl, string redirectUri, CancellationToken ct = default)
    {
        LastBaseUrl = baseUrl;
        var state = Guid.NewGuid().ToString("N");
        var start = new AbsSsoStart(
            $"https://idp.example/authorize?state={state}",
            new Dictionary<string, string> { ["connect.sid"] = $"abs-session-{state}" },
            state,
            CodeVerifier: $"verifier-{state}");
        _ssoStartsByState[state] = start;
        return Task.FromResult(start);
    }

    /// <summary>
    /// Behaves as Audiobookshelf does: the exchange only works when it is handed back exactly the
    /// verifier and session cookies from the start of the same flow, and only once.
    /// </summary>
    public Task<AbsLoginResponse> CompleteSsoAsync(
        string baseUrl, string code, string state, string codeVerifier,
        IReadOnlyDictionary<string, string> cookies, CancellationToken ct = default)
    {
        var isKnownFlow = _ssoStartsByState.Remove(state, out var start);
        if (!isKnownFlow || start!.CodeVerifier != codeVerifier || !cookies.SequenceEqual(start.Cookies))
            throw new HttpRequestException("No session");

        LastBaseUrl = baseUrl;
        var user = CurrentUserResponse().User with { AccessToken = "sso-access-token", RefreshToken = "sso-refresh-token" };
        return Task.FromResult(new AbsLoginResponse(user, "lib-1"));
    }

    private AbsLoginResponse CurrentUserResponse()
    {
        var user = new AbsUser(
            Id: "abs-user-1",
            Username: Username,
            Type: "user",
            Token: "abs-token",
            IsActive: true,
            Permissions: null,
            LibrariesAccessible: ["lib-1"],
            AccessToken: null,
            RefreshToken: null);
        return new AbsLoginResponse(user, "lib-1");
    }

    public Task<bool> ValidateTokenAsync(string baseUrl, string token, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<AbsLibrary[]> GetLibrariesAsync(string baseUrl, string token, CancellationToken ct = default) =>
        Task.FromResult<AbsLibrary[]>([new AbsLibrary("lib-1", "Books", "book", null)]);

    public Task<AbsLoginResponse> RefreshTokenAsync(string baseUrl, string refreshToken, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AbsLibraryItemsResponse> GetLibraryItemsAsync(string baseUrl, string token, string libraryId, int page = 0, int limit = 20, string? sort = null, bool desc = false, bool collapseSeries = false, string? search = null, string? filter = null, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AbsLibraryItem[]> SearchLibraryItemsAsync(string baseUrl, string token, string libraryId, string query, int limit = 20, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AbsLibraryItem> GetLibraryItemAsync(string baseUrl, string token, string itemId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<Stream> GetCoverImageAsync(string baseUrl, string token, string itemId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AbsSeriesResponse> GetSeriesAsync(string baseUrl, string token, string libraryId, int page = 0, int limit = 20, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AbsSeriesItem> GetSeriesDetailAsync(string baseUrl, string token, string libraryId, string seriesId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<Stream> DownloadAudioFileAsync(string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<(Stream Stream, long ContentLength, string ContentType)> DownloadAudioFileWithMetadataAsync(string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
