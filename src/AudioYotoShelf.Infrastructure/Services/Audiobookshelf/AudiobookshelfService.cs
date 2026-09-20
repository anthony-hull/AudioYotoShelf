using System.Net.Http.Headers;
using System.Net.Http.Json;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services.Audiobookshelf;

public class AudiobookshelfService(
    IHttpClientFactory httpClientFactory,
    ILogger<AudiobookshelfService> logger) : IAudiobookshelfService
{
    // ABS only routes POST here; a GET falls through to a 404.
    private const string AuthorizePath = "/api/authorize";

    private HttpClient CreateClient(string baseUrl, string token)
    {
        var client = httpClientFactory.CreateClient("Audiobookshelf");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<AbsLoginResponse> LoginAsync(string baseUrl, string username, string password, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("Audiobookshelf");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/'));

        // We don't keep a cookie jar, so ask ABS (v2.26+) to return the refresh token in the body.
        // Older servers ignore the header and return only the legacy opaque token.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = JsonContent.Create(new AbsLoginRequest(username, password))
        };
        request.Headers.Add("x-return-tokens", "true");

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsLoginResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize ABS login response");
    }

    public async Task<AbsLoginResponse> RefreshTokenAsync(string baseUrl, string refreshToken, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("Audiobookshelf");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/'));

        // ABS accepts the refresh token via the x-refresh-token header for cookie-less clients,
        // and returns the same payload as /login (with x-return-tokens to echo the rotated token).
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("x-refresh-token", refreshToken);
        request.Headers.Add("x-return-tokens", "true");

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsLoginResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize ABS refresh response");
    }

    public async Task<AbsLoginResponse> AuthorizeApiKeyAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, apiKey);
        var response = await client.PostAsync(AuthorizePath, null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsLoginResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize ABS authorize response");
    }

    public async Task<bool> ValidateTokenAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(baseUrl, token);
            var response = await client.PostAsync(AuthorizePath, null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to validate ABS token for {BaseUrl}", baseUrl);
            return false;
        }
    }

    public async Task<AbsLibrary[]> GetLibrariesAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var response = await client.GetAsync("/api/libraries", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AbsLibrariesWrapper>(ct);
        return result?.Libraries ?? [];
    }

    public async Task<AbsLibraryItemsResponse> GetLibraryItemsAsync(
        string baseUrl, string token, string libraryId,
        int page = 0, int limit = 20, string? sort = null, bool desc = false,
        bool collapseSeries = false, string? search = null, string? filter = null,
        CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var queryParams = $"?page={page}&limit={limit}&minified=1";
        if (sort is not null) queryParams += $"&sort={sort}";
        if (desc) queryParams += "&desc=1";
        if (collapseSeries) queryParams += "&collapseseries=1";
        if (!string.IsNullOrWhiteSpace(search))
            queryParams += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrWhiteSpace(filter))
            queryParams += $"&filter={filter}";

        var response = await client.GetAsync($"/api/libraries/{libraryId}/items{queryParams}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsLibraryItemsResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize library items");
    }

    public async Task<AbsLibraryItem[]> SearchLibraryItemsAsync(
        string baseUrl, string token, string libraryId, string query, int limit = 20, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var url = $"/api/libraries/{libraryId}/search?q={Uri.EscapeDataString(query)}&limit={limit}";

        var result = await client.GetFromJsonAsync<AbsSearchResponse>(url, ct);

        return (result?.Book ?? [])
            .Select(match => match.LibraryItem)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    public async Task<AbsLibraryItem> GetLibraryItemAsync(string baseUrl, string token, string itemId, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var response = await client.GetAsync($"/api/items/{itemId}?expanded=1", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsLibraryItem>(ct)
            ?? throw new InvalidOperationException($"Failed to deserialize library item {itemId}");
    }

    public async Task<Stream> GetCoverImageAsync(string baseUrl, string token, string itemId, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var response = await client.GetAsync($"/api/items/{itemId}/cover", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<AbsSeriesResponse> GetSeriesAsync(
        string baseUrl, string token, string libraryId,
        int page = 0, int limit = 20, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);
        var response = await client.GetAsync($"/api/libraries/{libraryId}/series?page={page}&limit={limit}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AbsSeriesResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize series response");
    }

    public async Task<AbsSeriesItem> GetSeriesDetailAsync(
        string baseUrl, string token, string libraryId, string seriesId, CancellationToken ct = default)
    {
        using var client = CreateClient(baseUrl, token);

        // /api/series/{id} returns the series name/description but NOT its books. The books are
        // fetched via the library items endpoint filtered by series, sorted by sequence. We reuse
        // GetLibraryItemsAsync (minified=1) on purpose: the full/expanded item shape can contain
        // fields that don't match our DTOs (e.g. a numeric publishedYear) and break deserialization.
        var meta = await client.GetFromJsonAsync<AbsSeriesMeta>($"/api/series/{seriesId}", ct);

        var filter = Uri.EscapeDataString(
            "series." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(seriesId)));
        var itemsResponse = await GetLibraryItemsAsync(
            baseUrl, token, libraryId, page: 0, limit: 500, sort: "sequence",
            desc: false, collapseSeries: false, search: null, filter: filter, ct: ct);

        var results = itemsResponse.Results ?? [];
        var books = results
            .Select(item => new AbsSeriesBook(
                item.Id,
                item.Media,
                item.Media?.Metadata?.Series?.FirstOrDefault(s => s.Id == seriesId)?.Sequence))
            .ToArray();

        var totalDuration = (int)results.Sum(i => i.Media?.Duration ?? 0);

        logger.LogInformation("Series {SeriesId} '{Name}' resolved to {Count} books in library {LibraryId}",
            seriesId, meta?.Name, books.Length, libraryId);

        return new AbsSeriesItem(
            seriesId,
            meta?.Name ?? "Unknown Series",
            meta?.Description,
            books,
            totalDuration);
    }

    public async Task<Stream> DownloadAudioFileAsync(
        string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default)
    {
        var (stream, _, _) = await DownloadAudioFileWithMetadataAsync(baseUrl, token, itemId, fileIno, ct);
        return stream;
    }

    public async Task<(Stream Stream, long ContentLength, string ContentType)> DownloadAudioFileWithMetadataAsync(
        string baseUrl, string token, string itemId, string fileIno, CancellationToken ct = default)
    {
        // Use ResponseHeadersRead to avoid buffering the entire file in memory
        var client = CreateClient(baseUrl, token);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/items/{itemId}/file/{fileIno}/download");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? -1;
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        var stream = await response.Content.ReadAsStreamAsync(ct);

        logger.LogInformation("Streaming audio file {FileIno} from item {ItemId}: {ContentLength} bytes, {ContentType}",
            fileIno, itemId, contentLength, contentType);

        return (stream, contentLength, contentType);
    }

    // Internal wrapper for the libraries endpoint response shape
    private record AbsLibrariesWrapper(AbsLibrary[] Libraries);

    // /api/series/{id} returns only the series-level fields (no books)
    private record AbsSeriesMeta(string Id, string Name, string? Description);

    // /api/libraries/{id}/search response shape (only the book matches are needed here)
    private record AbsSearchResponse(AbsSearchBookMatch[]? Book);
    private record AbsSearchBookMatch(AbsLibraryItem? LibraryItem);
}
