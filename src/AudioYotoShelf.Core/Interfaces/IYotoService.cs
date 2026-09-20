using AudioYotoShelf.Core.DTOs.Yoto;

namespace AudioYotoShelf.Core.Interfaces;

/// <summary>
/// Handles all communication with the Yoto API including OAuth, content management,
/// audio upload pipeline, and icon management.
/// </summary>
public interface IYotoService
{
    // OAuth Authorization Code Flow
    string GetAuthorizationUrl(string redirectUri, string state);
    Task<YotoTokenResponse> ExchangeAuthCodeAsync(string code, string redirectUri, CancellationToken ct = default);
    Task<YotoTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    // Cards
    Task<YotoCard[]> GetUserCardsAsync(string accessToken, CancellationToken ct = default);
    Task<YotoCard> GetCardContentAsync(string accessToken, string cardId, CancellationToken ct = default);
    Task<string> CreateOrUpdateCardAsync(string accessToken, YotoCardContent content, YotoCardMetadata metadata, string? title = null, string? existingCardId = null, CancellationToken ct = default);
    Task DeleteCardAsync(string accessToken, string cardId, CancellationToken ct = default);

    // Audio Upload Pipeline (4-step)
    Task<YotoUploadInfo> GetUploadUrlAsync(string accessToken, CancellationToken ct = default);
    Task UploadAudioFileAsync(string uploadUrl, Stream audioStream, long contentLength, string contentType, CancellationToken ct = default);
    /// <summary>
    /// Waits for Yoto to finish transcoding an upload. <paramref name="progress"/> receives Yoto's own
    /// 0-100 percentage, each time it changes.
    /// </summary>
    Task<YotoTranscodeResponse> PollTranscodeStatusAsync(string accessToken, string uploadId, IProgress<int>? progress = null, CancellationToken ct = default);
    Task<string> UploadAndTranscodeAsync(string accessToken, Stream audioStream, long contentLength, string contentType, IProgress<int>? progress = null, CancellationToken ct = default);

    // Icons
    Task<YotoPublicIcon[]> GetPublicIconsAsync(string accessToken, CancellationToken ct = default);
    Task<YotoIconUploadResponse> UploadCustomIconAsync(string accessToken, byte[] iconData, string filename, CancellationToken ct = default);

    // Cover
    Task<string> UploadCoverImageAsync(string accessToken, Stream imageStream, CancellationToken ct = default);
}
