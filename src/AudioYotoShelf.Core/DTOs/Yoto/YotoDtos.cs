using System.Text.Json.Serialization;

namespace AudioYotoShelf.Core.DTOs.Yoto;

// --- OAuth ---

public record YotoTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string? ClientSecret,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken
);

public record YotoTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn
);

// --- Card Content ---

public record YotoCardListResponse(
    YotoCard[] Cards
);

public record YotoCard(
    string CardId,
    YotoCardContent? Content,
    YotoCardMetadata? Metadata,
    // Top-level card name shown in the Yoto app (separate from metadata.description).
    string? Title = null
);

public record YotoCardContent(
    YotoChapter[] Chapters,
    YotoCardConfig? Config,
    string? PlaybackType,
    // Yoto validates this as a string ("1"), not a number.
    string? Version
);

public record YotoCardConfig(
    bool? AllowSkip,
    bool? AllowFastForward,
    bool? AllowRewind
);

public record YotoChapter(
    string Key,
    string Title,
    YotoTrack[] Tracks,
    YotoDisplay? Display
);

public record YotoTrack(
    string Key,
    string Title,
    string TrackUrl,
    string? Format,
    string? Type,
    double? Duration,
    long? FileSize,
    string? Channels,
    YotoDisplay? Display
);

public record YotoDisplay(
    [property: JsonPropertyName("icon16x16")] string? Icon16X16
);

public record YotoCardMetadata(
    string? Author,
    string? Category,
    string? Description,
    string[]? Genre,
    string[]? Languages,
    int? MinAge,
    int? MaxAge,
    string? ReadBy,
    YotoCover? Cover
);

public record YotoCover(
    string? ImageL
);

// --- Media Upload ---

public record YotoUploadUrlResponse(
    YotoUploadInfo Upload
);

public record YotoUploadInfo(
    string UploadUrl,
    string UploadId
);

/// <param name="Phase">Yoto's own stage, for example "transcoding" (from <c>transcode.progress.phase</c>).</param>
/// <param name="Percent">How far Yoto's transcode has got, 0-100 (from <c>transcode.progress.percent</c>); null before it reports one.</param>
public record YotoTranscodeResponse(
    string? TranscodedSha256,
    YotoTranscodedInfo? TranscodedInfo,
    string? Status,
    string? Phase = null,
    int? Percent = null
);

public record YotoTranscodedInfo(
    double Duration,
    long FileSize,
    string Channels,
    string Format
);

// --- Icons ---

public record YotoPublicIcon(
    string MediaId,
    string Title,
    string[] PublicTags,
    string Url
);

public record YotoIconUploadResponse(
    string MediaId,
    string Url
);

// --- Cover ---

public record YotoCoverUploadResponse(
    string Url
);
