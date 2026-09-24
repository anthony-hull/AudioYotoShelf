using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AudioYotoShelf.Core;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services.Yoto;

public class YotoService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<YotoService> logger) : IYotoService
{
    private const int MaxTranscodePollAttempts = 360;
    private const int MaxPercent = 100;
    private const int TranscodePollDelayMs = 5000;
    private const int MaxUploadAttempts = 4;
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    // Base URLs are configurable (Yoto:ApiBase / Yoto:AuthBase) so tests/E2E can point them at a
    // mock Yoto server; they default to the real Yoto endpoints in production.
    // Yoto grants only a default (user:account:view) to a client that does not ask, and refuses
    // uploads with "User does not have required scope(s): 'user:content:manage'". Ask for what the
    // app calls: content (upload audio, create/update/delete cards, list the person's own),
    // and icons (upload custom ones).
    private const string OAuthScopes =
        "profile offline_access openid user:content:manage user:content:view user:icons:manage";

    private string YotoApiBase => configuration["Yoto:ApiBase"] ?? "https://api.yotoplay.com";
    private string YotoAuthBase => configuration["Yoto:AuthBase"] ?? "https://login.yotoplay.com";

    private string ClientId => configuration["Yoto:ClientId"]
        ?? throw new InvalidOperationException("Yoto:ClientId not configured");
    private string? ClientSecret => configuration["Yoto:ClientSecret"];

    private HttpClient CreateApiClient(string accessToken)
    {
        var client = httpClientFactory.CreateClient("Yoto");
        client.BaseAddress = new Uri(YotoApiBase);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // --- OAuth Authorization Code Flow ---

    public string GetAuthorizationUrl(string redirectUri, string state)
    {
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = ClientId;
        query["redirect_uri"] = redirectUri;
        query["scope"] = OAuthScopes;
        query["audience"] = YotoApiBase;
        query["state"] = state;
        return $"{YotoAuthBase}/authorize?{query}";
    }

    public async Task<YotoTokenResponse> ExchangeAuthCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("YotoAuth");
        client.BaseAddress = new Uri(YotoAuthBase);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
        };
        if (!string.IsNullOrEmpty(ClientSecret))
            form["client_secret"] = ClientSecret;

        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Yoto auth code exchange failed: {StatusCode} {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadFromJsonAsync<YotoTokenResponse>(ct)
            ?? throw new InvalidOperationException("Failed to deserialize token response");
    }

    public async Task<YotoTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        using var client = httpClientFactory.CreateClient("YotoAuth");
        client.BaseAddress = new Uri(YotoAuthBase);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        if (!string.IsNullOrEmpty(ClientSecret))
            form["client_secret"] = ClientSecret;

        var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<YotoTokenResponse>(ct)
            ?? throw new InvalidOperationException("Failed to refresh Yoto token");
    }

    // --- Cards ---

    public async Task<YotoCard[]> GetUserCardsAsync(string accessToken, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);

        // Probe candidate "list my cards" endpoints; the family-library one 403s for MYO content.
        foreach (var path in new[] { "/content/mine", "/card/family/library/mine?showDeleted=false" })
        {
            var response = await client.GetAsync(path, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            logger.LogDebug("Cards-list {Path} -> {Status}", path, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode) continue;

            var result = JsonSerializer.Deserialize<YotoCardListResponse>(json, JsonWeb);
            return result?.Cards ?? [];
        }

        throw new HttpRequestException("No Yoto card-list endpoint succeeded", null, System.Net.HttpStatusCode.Forbidden);
    }

    public async Task<YotoCard> GetCardContentAsync(string accessToken, string cardId, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        var response = await client.GetAsync($"/content/{cardId}", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        logger.LogDebug("Yoto card content {CardId} response: {Body}", cardId, json.Length > 700 ? json[..700] : json);

        // GET /content/{id} nests the card under a "card" object and omits the id from the body.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cardNode = root.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;

        var content = cardNode.TryGetProperty("content", out var ce) && ce.ValueKind == JsonValueKind.Object
            ? ce.Deserialize<YotoCardContent>(JsonWeb) : null;
        var metadata = cardNode.TryGetProperty("metadata", out var me) && me.ValueKind == JsonValueKind.Object
            ? me.Deserialize<YotoCardMetadata>(JsonWeb) : null;
        var title = cardNode.TryGetProperty("title", out var te) && te.ValueKind == JsonValueKind.String
            ? te.GetString() : null;

        return new YotoCard(cardId, content, metadata, title);
    }

    public async Task<string> CreateOrUpdateCardAsync(
        string accessToken, YotoCardContent content, YotoCardMetadata metadata,
        string? title = null, string? existingCardId = null, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);

        // 'title' is the top-level card name shown in the Yoto app/editor (separate from metadata.description).
        var body = new { cardId = existingCardId, title, content, metadata };
        var response = await client.PostAsJsonAsync("/content", body, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // The card we tried to update was deleted on Yoto — create a fresh one instead.
            if (existingCardId is not null && responseBody.Contains("Deleted card", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Yoto card {CardId} was deleted; creating a new card instead", existingCardId);
                return await CreateOrUpdateCardAsync(accessToken, content, metadata, title, null, ct);
            }

            logger.LogError("Yoto card create/update failed: {StatusCode} {Body}", response.StatusCode, responseBody);
            response.EnsureSuccessStatusCode();
        }

        var cardId = ExtractCardId(responseBody);
        // Stryker disable once Equality : the condition only gates a warning log line
        if (cardId is null)
            logger.LogWarning("Yoto card create/update returned no recognizable cardId: {Body}",
                responseBody.Length > 600 ? responseBody[..600] : responseBody);

        return cardId ?? throw new InvalidOperationException("No cardId returned from Yoto");
    }

    public async Task DeleteCardAsync(string accessToken, string cardId, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        var response = await client.DeleteAsync($"/content/{cardId}", ct);
        response.EnsureSuccessStatusCode();
    }

    // --- Audio Upload Pipeline ---

    public async Task<YotoUploadInfo> GetUploadUrlAsync(string accessToken, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        var response = await client.GetAsync("/media/transcode/audio/uploadUrl", ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<YotoUploadUrlResponse>(ct);
        return result?.Upload ?? throw new InvalidOperationException("No upload URL returned");
    }

    public async Task UploadAudioFileAsync(
        string uploadUrl, Stream audioStream, long contentLength, string contentType, CancellationToken ct = default)
    {
        // The upload target is a presigned (S3-style) URL that closes idle keep-alive
        // connections; a pooled-but-stale socket surfaces as "broken pipe" mid-write.
        // Rewind and retry transient transport failures — the source is always a seekable file.
        for (var attempt = 1; ; attempt++)
        {
            if (audioStream.CanSeek) audioStream.Position = 0;

            using var client = httpClientFactory.CreateClient("YotoUpload");
            // StreamContent disposes the wrapped stream on its own disposal; the caller owns
            // audioStream (and disposes it), so this content is intentionally not in a 'using' —
            // disposing it here would close the stream before a retry could rewind it.
            var content = new StreamContent(audioStream);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (contentLength > 0) content.Headers.ContentLength = contentLength;

            try
            {
                using var response = await client.PutAsync(uploadUrl, content, ct);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException ex) when (attempt < MaxUploadAttempts && audioStream.CanSeek && IsTransientUploadError(ex))
            {
                logger.LogWarning(ex,
                    "Yoto upload PUT failed (attempt {Attempt}/{Max}); retrying after backoff",
                    attempt, MaxUploadAttempts);
                await DelayBetweenUploadAttemptsAsync(attempt, ct);
            }
        }
    }

    // Seam for tests: production waits TranscodePollDelayMs between polls; tests do not wait.
    protected virtual Task DelayBetweenTranscodePollsAsync(CancellationToken ct) =>
        Task.Delay(TranscodePollDelayMs, ct);

    // Seam for tests: backoff is 2s, 4s, 8s in production; overridden to no-op in unit tests.
    protected virtual Task DelayBetweenUploadAttemptsAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);

    /// <summary>
    /// A reset/closed connection during the request-body write throws HttpRequestException
    /// wrapping IOException → SocketException (broken pipe / connection reset). These are safe
    /// to retry; a non-success HTTP status (EnsureSuccessStatusCode) is not and falls through.
    /// </summary>
    private static bool IsTransientUploadError(HttpRequestException ex)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionReset
                    or SocketError.Shutdown or SocketError.ConnectionAborted)
                return true;
            if (inner is IOException) return true;
        }
        return false;
    }

    public async Task<YotoTranscodeResponse> PollTranscodeStatusAsync(
        string accessToken, string uploadId, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        int? lastReportedPercent = null;

        for (var attempt = 0; attempt < MaxTranscodePollAttempts; attempt++)
        {
            var response = await client.GetAsync(
                $"/media/upload/{uploadId}/transcoded?loudnorm=false", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);

            // Raw body once per upload (Debug) for diagnosing response-shape changes.
            // Stryker disable once Equality : the condition only gates a debug log line
            if (attempt == 0)
                logger.LogDebug("Transcode response for {UploadId}: {Body}",
                    uploadId, json.Length > 600 ? json[..600] : json);

            var result = ParseTranscodeResponse(json);
            if (result.TranscodedSha256 is not null)
            {
                logger.LogInformation("Transcode complete for upload {UploadId}: SHA256={Sha256}",
                    uploadId, result.TranscodedSha256);
                return result;
            }

            if (result.Percent is { } percent && percent != lastReportedPercent)
            {
                lastReportedPercent = percent;
                progress?.Report(percent);
            }

            if (attempt % 10 == 0)
                logger.LogInformation("Transcode poll {Attempt}/{Max} for {UploadId}: phase={Phase} percent={Percent}",
                    attempt, MaxTranscodePollAttempts, uploadId, result.Phase ?? "unknown", result.Percent?.ToString() ?? "unknown");

            await DelayBetweenTranscodePollsAsync(ct);
        }

        var elapsedMinutes = MaxTranscodePollAttempts * TranscodePollDelayMs / 60_000.0;
        throw new TimeoutException($"Transcode polling timed out for upload {uploadId} after {MaxTranscodePollAttempts} attempts (~{elapsedMinutes:F0} min)");
    }

    /// <summary>
    /// The POST /content response returns the card id, but the exact key/nesting varies
    /// (cardId / id / contentId, sometimes under a "card" wrapper). Find it tolerantly.
    /// </summary>
    private static string? ExtractCardId(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return FindCardId(doc.RootElement, depth: 0);
    }

    private static string? FindCardId(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 3) return null;

        foreach (var name in (ReadOnlySpan<string>)["cardId", "id", "contentId"])
            if (element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var found = FindCardId(prop.Value, depth + 1);
                if (found is not null) return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Yoto nests the transcode payload under a "transcode" object (like the upload endpoint nests
    /// under "upload"); older/other shapes put it at the root. Read whichever is present.
    /// </summary>
    private static YotoTranscodeResponse ParseTranscodeResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var node = root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("transcode", out var transcode)
                   && transcode.ValueKind == JsonValueKind.Object
            ? transcode
            : root;

        string? Str(string name) =>
            node.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        var (phase, percent) = ReadYotoProgress(node);
        return new YotoTranscodeResponse(
            Str("transcodedSha256"),
            ReadTranscodedInfo(node),
            Str("status") ?? Str("transcodeStatus"),
            phase,
            percent);
    }

    /// <summary>
    /// What Yoto actually produced — duration, size, channels and (critically) the real codec/format,
    /// which does not always match what we declared on upload. Absent, or missing any one field, counts
    /// as no info at all: a caller falling back to its own default is safer than one field being wrong.
    /// </summary>
    private static YotoTranscodedInfo? ReadTranscodedInfo(JsonElement node)
    {
        if (!node.TryGetProperty("transcodedInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        var hasDuration = info.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number;
        var hasFileSize = info.TryGetProperty("fileSize", out var fileSize) && fileSize.ValueKind == JsonValueKind.Number;
        var channels = info.TryGetProperty("channels", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var format = info.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        if (!hasDuration || !hasFileSize || channels is null || format is null)
            return null;

        return new YotoTranscodedInfo(duration.GetDouble(), fileSize.GetInt64(), channels, format);
    }

    /// <summary>Yoto reports how far it has got under <c>progress: { phase, percent }</c>.</summary>
    private static (string? Phase, int? Percent) ReadYotoProgress(JsonElement transcode)
    {
        if (!transcode.TryGetProperty("progress", out var progress) || progress.ValueKind != JsonValueKind.Object)
            return (null, null);

        var phase = progress.TryGetProperty("phase", out var phaseElement) && phaseElement.ValueKind == JsonValueKind.String
            ? phaseElement.GetString()
            : null;
        var hasPercent = progress.TryGetProperty("percent", out var percentElement)
                         && percentElement.ValueKind == JsonValueKind.Number
                         && percentElement.TryGetDouble(out _);

        // Kept inside 0-100 here so nothing downstream has to trust what Yoto sends.
        return (phase, hasPercent ? Math.Clamp((int)Math.Round(percentElement.GetDouble()), 0, MaxPercent) : null);
    }


    public async Task<YotoTranscodeResult> UploadAndTranscodeAsync(
        string accessToken, Stream audioStream, long contentLength, string contentType,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Step 1: Get upload URL
        progress?.Report(10);
        var uploadInfo = await GetUploadUrlAsync(accessToken, ct);

        // Step 2: Upload file
        progress?.Report(30);
        await UploadAudioFileAsync(uploadInfo.UploadUrl, audioStream, contentLength, contentType, ct);

        // Step 3: Poll for transcode completion
        progress?.Report(YotoUploadProgress.TranscodeStart);
        var transcodeResult = await PollTranscodeStatusAsync(
            accessToken, uploadInfo.UploadId,
            progress is null ? null : new InlineProgress<int>(yoto => progress.Report(YotoUploadProgress.FromTranscodePercent(yoto))),
            ct);

        progress?.Report(YotoUploadProgress.Complete);
        var info = transcodeResult.TranscodedInfo;
        return new YotoTranscodeResult(transcodeResult.TranscodedSha256!, info?.Format, info?.Duration, info?.FileSize);
    }

    // --- Icons ---

    public async Task<YotoPublicIcon[]> GetPublicIconsAsync(string accessToken, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        var response = await client.GetAsync("/media/displayIcons/public", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<YotoPublicIcon[]>(ct) ?? [];
    }

    public async Task<YotoIconUploadResponse> UploadCustomIconAsync(
        string accessToken, byte[] iconData, string filename, CancellationToken ct = default)
    {
        using var client = CreateApiClient(accessToken);
        using var formContent = new MultipartFormDataContent();
        formContent.Add(new ByteArrayContent(iconData), "file", filename);

        var response = await client.PostAsync(
            $"/media/displayIcons/user/me/upload?autoConvert=true&filename={Uri.EscapeDataString(filename)}", formContent, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<YotoIconUploadResponse>(ct)
            ?? throw new InvalidOperationException("Failed to upload custom icon");
    }

    // --- Cover ---

    public async Task<string> UploadCoverImageAsync(string accessToken, Stream imageStream, CancellationToken ct = default)
    {
        // Buffer so we can retry the upload across candidate endpoints.
        using var buffer = new MemoryStream();
        await imageStream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        using var client = CreateApiClient(accessToken);

        // The cover endpoint wants the raw image bytes as the request body (not multipart);
        // it parallels the icon endpoint but takes a binary body.
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var response = await client.PostAsync(
            "/media/coverImage/user/me/upload?autoConvert=true&coverType=default", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        logger.LogInformation("Cover upload -> {Status}: {Body}", (int)response.StatusCode, json.Length > 400 ? json[..400] : json);
        response.EnsureSuccessStatusCode();

        return ExtractCoverUrl(json) ?? throw new InvalidOperationException("No cover URL in Yoto response");
    }

    /// <summary>
    /// Finds the cover image URL in the upload response, tolerating Yoto's wrapper nesting
    /// (e.g. { "coverImage": { "mediaUrl": ... } }) and varied key names.
    /// </summary>
    private static string? ExtractCoverUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        IEnumerable<JsonElement> nodes = [root, .. new[] { "coverImage", "cover", "upload", "media" }
            .Where(w => root.TryGetProperty(w, out var e) && e.ValueKind == JsonValueKind.Object)
            .Select(w => root.GetProperty(w))];

        foreach (var node in nodes)
            foreach (var key in new[] { "mediaUrl", "imageL", "url", "coverImageL" })
                if (node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        return null;
    }
}
