using AudioYotoShelf.Core.Enums;

namespace AudioYotoShelf.Core.DTOs.Transfer;

// --- Transfer Requests ---

public record CreateTransferRequest(
    string AbsLibraryItemId,
    YotoCategory Category = YotoCategory.Stories,
    PlaybackType PlaybackType = PlaybackType.Linear,
    int? OverrideMinAge = null,
    int? OverrideMaxAge = null
);

public record CreateSeriesTransferRequest(
    string AbsSeriesId,
    string AbsLibraryId,
    YotoCategory Category = YotoCategory.Stories,
    bool OneCardPerBook = true,
    int? OverrideMinAge = null,
    int? OverrideMaxAge = null
);

public record BatchTransferRequest(
    string[] AbsLibraryItemIds,
    YotoCategory Category = YotoCategory.Stories,
    PlaybackType PlaybackType = PlaybackType.Linear,
    int? OverrideMinAge = null,
    int? OverrideMaxAge = null
);

public record BatchTransferResponse(
    string BatchId,
    int TotalBooks,
    int Queued,
    string[] JobIds
);

// --- Settings ---

public record UpdateSettingsRequest(
    string? DefaultLibraryId = null,
    int? DefaultMinAge = null,
    int? DefaultMaxAge = null
);

// --- Transfer Responses ---

public record TransferResponse(
    Guid Id,
    string AbsLibraryItemId,
    string BookTitle,
    string? BookAuthor,
    string? SeriesName,
    float? SeriesSequence,
    TransferStatus Status,
    int ProgressPercent,
    string? ErrorMessage,
    AgeRangeResponse AgeRange,
    string? YotoCardId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    TrackMappingResponse[] Tracks
);

public record AgeRangeResponse(
    int SuggestedMin,
    int SuggestedMax,
    string SuggestionReason,
    AgeRangeSource SuggestionSource,
    int? OverrideMin,
    int? OverrideMax,
    int EffectiveMin,
    int EffectiveMax
);

public record TrackMappingResponse(
    Guid Id,
    string ChapterTitle,
    int ChapterIndex,
    double Duration,
    bool IsUploaded,
    string? IconUrl
);

// --- Progress ---

/// <param name="TrackId">The track this update is about, when it is about one; null for the transfer as a whole.</param>
/// <param name="TrackPhase">Which stage that track is at.</param>
/// <param name="TrackPercent">How far Yoto's transcode of that track has got, 0-100, while it is transcoding.</param>
public record TransferProgressUpdate(
    Guid TransferId,
    TransferStatus Status,
    int ProgressPercent,
    string? CurrentStep,
    string? ErrorMessage,
    Guid? TrackId = null,
    TrackPhase? TrackPhase = null,
    int? TrackPercent = null
);

// --- Age Suggestion ---

public record AgeSuggestionResponse(
    int SuggestedMinAge,
    int SuggestedMaxAge,
    string Reason,
    AgeRangeSource Source,
    AgeSuggestionDetail[] Signals
);

public record AgeSuggestionDetail(
    string Signal,
    string Value,
    int Weight
);
