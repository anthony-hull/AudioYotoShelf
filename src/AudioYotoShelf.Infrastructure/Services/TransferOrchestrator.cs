using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services;

public class TransferOrchestrator(
    AudioYotoShelfDbContext db,
    IAudiobookshelfService absService,
    IYotoService yotoService,
    IIconGenerationService iconService,
    IAgeSuggestionService ageService,
    IChapterExtractor chapterExtractor,
    ITransferProgressNotifier notifier,
    IConfiguration configuration,
    TransferMetrics metrics,
    ILogger<TransferOrchestrator> logger) : ITransferOrchestrator
{
    // The ErrorMessage column is bounded; a longer message is cut rather than failing the save.
    private const int MaxStoredErrorLength = 4000;

    private string TempDir => configuration.GetValue("Transfer:TempDirectory", "/app/temp")!;

    public async Task<TransferResponse> TransferBookAsync(
        Guid userConnectionId, CreateTransferRequest request, Guid? transferId = null, CancellationToken ct = default)
    {
        var user = await db.UserConnections.FindAsync([userConnectionId], ct)
            ?? throw new InvalidOperationException("User connection not found");

        EnsureValidConnections(user);

        // Refresh the stored ABS access token up front so every download in this job uses a fresh
        // token (mutates user.AudiobookshelfToken in place; no-op for legacy/opaque tokens).
        await AbsTokens.EnsureValidAsync(db, absService, user, logger, ct);

        logger.LogInformation("Starting transfer for item {ItemId}, user {UserId}",
            request.AbsLibraryItemId, userConnectionId);

        // Create (or load, on Hangfire retry) the transfer record up front so that a failure
        // while fetching the item is still recorded as Failed and visible in the user's history.
        CardTransfer? transfer = transferId.HasValue
            ? await db.CardTransfers.FirstOrDefaultAsync(t => t.Id == transferId.Value, ct)
            : null;

        if (transfer is not null)
        {
            // If cancelled (e.g. user cancelled during a previous Hangfire attempt), don't re-run
            if (transfer.Status == TransferStatus.Cancelled)
                return MapToResponse(transfer);

            // Reset the existing transfer for retry
            transfer.Status = TransferStatus.Pending;
            transfer.ErrorMessage = null;
            transfer.ProgressPercent = 0;
            transfer.CompletedAt = null;

            // Clean up any track mappings from the previous attempt
            var oldMappings = await db.TrackMappings
                .Where(tm => tm.CardTransferId == transfer.Id)
                .ToListAsync(ct);
            db.TrackMappings.RemoveRange(oldMappings);
        }
        else
        {
            // Title/age are placeholders until the item metadata is fetched below.
            transfer = new CardTransfer
            {
                Id = transferId ?? Guid.NewGuid(),
                UserConnectionId = userConnectionId,
                AbsLibraryItemId = request.AbsLibraryItemId,
                BookTitle = "(pending)",
                Category = request.Category,
                PlaybackType = request.PlaybackType,
                AgeSuggestionReason = "(pending)",
                OverrideMinAge = request.OverrideMinAge,
                OverrideMaxAge = request.OverrideMaxAge,
                Status = TransferStatus.Pending
            };
            db.CardTransfers.Add(transfer);
        }

        await db.SaveChangesAsync(ct);

        try
        {
            var item = await absService.GetLibraryItemAsync(
                user.AudiobookshelfUrl, user.AudiobookshelfToken!, request.AbsLibraryItemId, ct);

            var media = item.Media ?? throw new InvalidOperationException("Item has no media");
            var metadata = media.Metadata;

            var ageSuggestion = ageService.SuggestAgeRange(metadata, media.Duration, media.NumChapters);

            // Enrich the record now that metadata is available
            transfer.BookTitle = metadata.Title ?? "Unknown";
            transfer.BookAuthor = metadata.Authors?.FirstOrDefault()?.Name;
            transfer.SeriesName = metadata.Series?.FirstOrDefault()?.Name;
            transfer.SeriesSequence = ParseSequence(metadata.Series?.FirstOrDefault()?.Sequence);
            transfer.SuggestedMinAge = ageSuggestion.SuggestedMinAge;
            transfer.SuggestedMaxAge = ageSuggestion.SuggestedMaxAge;
            transfer.AgeSuggestionReason = ageSuggestion.Reason;
            transfer.AgeSuggestionSource = ageSuggestion.Source;
            await db.SaveChangesAsync(ct);

            await UpdateStatus(transfer, TransferStatus.DownloadingAudio, 5, ct);
            var (trackMappings, chapterPaths) = await BuildTrackMappingsAsync(user, item, transfer, ct);

            await UpdateStatus(transfer, TransferStatus.UploadingToYoto, 20, ct);
            var yotoAccessToken = await EnsureYotoTokenAsync(user, ct);
            await UploadTracksAsync(yotoAccessToken, trackMappings, chapterPaths, transfer, ct);

            await UpdateStatus(transfer, TransferStatus.GeneratingIcons, 70, ct);
            var chapterIcons = await GenerateIconsAsync(yotoAccessToken, user, media, trackMappings, ct);

            string? coverUrl = null;
            try
            {
                var coverStream = await absService.GetCoverImageAsync(
                    user.AudiobookshelfUrl, user.AudiobookshelfToken!,
                    request.AbsLibraryItemId, ct);
                coverUrl = await yotoService.UploadCoverImageAsync(yotoAccessToken, coverStream, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to upload cover art for {ItemId}", request.AbsLibraryItemId);
            }

            await UpdateStatus(transfer, TransferStatus.CreatingCard, 85, ct);
            var cardId = await CreateYotoCardAsync(
                yotoAccessToken, transfer, metadata, trackMappings, chapterIcons, coverUrl, ct);

            transfer.YotoCardId = cardId;
            transfer.Status = TransferStatus.Completed;
            transfer.ProgressPercent = 100;
            transfer.ErrorMessage = null; // clear any error from a prior failed attempt
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Transfer completed: {TransferId} → Card {CardId}", transfer.Id, cardId);
            metrics.RecordCompleted();
            await NotifyAsync(transfer, StepLabel(TransferStatus.Completed), CancellationToken.None);
            return MapToResponse(transfer);
        }
        catch (OperationCanceledException)
        {
            transfer.Status = TransferStatus.Cancelled;
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(transfer, StepLabel(TransferStatus.Cancelled), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transfer failed: {TransferId}", transfer.Id);
            metrics.RecordFailed();
            transfer.Status = TransferStatus.Failed;
            transfer.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, MaxStoredErrorLength)];
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyAsync(transfer, StepLabel(TransferStatus.Failed), CancellationToken.None);
            throw;
        }
        finally
        {
            CleanupTempFiles(transfer.Id);
        }
    }

    public async Task<TransferResponse[]> TransferSeriesAsync(
        Guid userConnectionId, CreateSeriesTransferRequest request, CancellationToken ct = default)
    {
        var user = await db.UserConnections.FindAsync([userConnectionId], ct)
            ?? throw new InvalidOperationException("User connection not found");

        EnsureValidConnections(user);

        await AbsTokens.EnsureValidAsync(db, absService, user, logger, ct);

        var seriesDetail = await absService.GetSeriesDetailAsync(
            user.AudiobookshelfUrl, user.AudiobookshelfToken!, request.AbsLibraryId, request.AbsSeriesId, ct);

        var results = new List<TransferResponse>();
        var orderedBooks = seriesDetail.Books
            .OrderBy(b => ParseSequence(b.Sequence) ?? 999f)
            .ToArray();

        logger.LogInformation("Transferring series '{SeriesName}' with {BookCount} books",
            seriesDetail.Name, orderedBooks.Length);

        foreach (var book in orderedBooks)
        {
            try
            {
                var bookRequest = new CreateTransferRequest(
                    AbsLibraryItemId: book.Id,
                    Category: request.Category,
                    OverrideMinAge: request.OverrideMinAge,
                    OverrideMaxAge: request.OverrideMaxAge
                );
                var result = await TransferBookAsync(userConnectionId, bookRequest, ct: ct);
                results.Add(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to transfer book {BookId} in series {SeriesName}",
                    book.Id, seriesDetail.Name);
            }
        }

        return results.ToArray();
    }

    public async Task<TransferResponse> RetryTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        var transfer = await db.CardTransfers
            .Include(t => t.TrackMappings)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct)
            ?? throw new InvalidOperationException("Transfer not found");

        if (transfer.Status is not (TransferStatus.Failed or TransferStatus.Cancelled))
            throw new InvalidOperationException("Can only retry failed or cancelled transfers");

        // Pre-reset so the Cancelled early-return in TransferBookAsync doesn't block us
        transfer.Status = TransferStatus.Pending;
        transfer.ErrorMessage = null;
        transfer.ProgressPercent = 0;
        transfer.CompletedAt = null;
        var oldMappings = await db.TrackMappings
            .Where(tm => tm.CardTransferId == transfer.Id)
            .ToListAsync(ct);
        db.TrackMappings.RemoveRange(oldMappings);
        await db.SaveChangesAsync(ct);

        var request = new CreateTransferRequest(
            AbsLibraryItemId: transfer.AbsLibraryItemId,
            Category: transfer.Category,
            PlaybackType: transfer.PlaybackType,
            OverrideMinAge: transfer.OverrideMinAge,
            OverrideMaxAge: transfer.OverrideMaxAge
        );

        return await TransferBookAsync(transfer.UserConnectionId, request, transfer.Id, ct);
    }

    public async Task CancelTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        var transfer = await db.CardTransfers.FindAsync([transferId], ct)
            ?? throw new InvalidOperationException("Transfer not found");

        transfer.Status = TransferStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        // Don't clean up temp files here — the running job's finally block handles cleanup.
        // Deleting files while a background job is still writing causes FileNotFoundException.
    }

    public async Task<TransferResponse> GetTransferStatusAsync(Guid transferId, CancellationToken ct = default)
    {
        var transfer = await db.CardTransfers
            .Include(t => t.TrackMappings)
                .ThenInclude(tm => tm.GeneratedIcon)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct)
            ?? throw new InvalidOperationException("Transfer not found");

        return MapToResponse(transfer);
    }

    public async Task<TransferResponse[]> GetUserTransfersAsync(
        Guid userConnectionId, int page = 0, int limit = 20, CancellationToken ct = default)
    {
        var transfers = await db.CardTransfers
            .Where(t => t.UserConnectionId == userConnectionId)
            .Include(t => t.TrackMappings)
                .ThenInclude(tm => tm.GeneratedIcon)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(page * limit)
            .Take(limit)
            .ToArrayAsync(ct);

        return transfers.Select(MapToResponse).ToArray();
    }

    // =========================================================================
    // Private pipeline methods
    // =========================================================================

    internal async Task<(List<TrackMapping> Mappings, Dictionary<int, string> ChapterPaths)> BuildTrackMappingsAsync(
        UserConnection user, AbsLibraryItem item, CardTransfer transfer, CancellationToken ct)
    {
        var media = item.Media!;
        var mappings = new List<TrackMapping>();
        var chapterPaths = new Dictionary<int, string>();

        if (media.AudioFiles.Length == 0)
            throw new InvalidOperationException("Item has no audio files to transfer");

        if (media.NumAudioFiles > 1)
        {
            // Multi-file book: each audio file = one track
            foreach (var audioFile in media.AudioFiles.OrderBy(f => f.Index))
            {
                var chapterTitle = media.Chapters.Length > audioFile.Index
                    ? media.Chapters[audioFile.Index].Title
                    : audioFile.Metadata.Filename;

                var mapping = new TrackMapping
                {
                    CardTransferId = transfer.Id,
                    AbsFileIno = audioFile.Ino,
                    ChapterTitle = chapterTitle,
                    ChapterIndex = audioFile.Index,
                    StartTime = 0,
                    EndTime = audioFile.Duration,
                    FileSizeBytes = audioFile.Size
                };

                db.TrackMappings.Add(mapping);
                mappings.Add(mapping);
            }
        }
        else if (media.Chapters.Length > 0 && media.AudioFiles.Length == 1)
        {
            // Single-file book: extract chapters via FFmpeg
            var singleFile = media.AudioFiles[0];

            // Download the single file to temp
            var tempInputPath = Path.Combine(TempDir, $"{transfer.Id}_input{Path.GetExtension(singleFile.Metadata.Filename)}");
            await DownloadToFileAsync(user, item.Id, singleFile.Ino, tempInputPath, ct);

            for (int i = 0; i < media.Chapters.Length; i++)
            {
                var chapter = media.Chapters[i];
                var chapterPath = await chapterExtractor.ExtractChapterAsync(
                    tempInputPath, chapter.Start, chapter.End, "m4a", ct);

                chapterPaths[i] = chapterPath;

                var mapping = new TrackMapping
                {
                    CardTransferId = transfer.Id,
                    AbsFileIno = $"{singleFile.Ino}:ch{i}",
                    ChapterTitle = chapter.Title,
                    ChapterIndex = i,
                    StartTime = chapter.Start,
                    EndTime = chapter.End,
                    FileSizeBytes = new FileInfo(chapterPath).Length
                };

                db.TrackMappings.Add(mapping);
                mappings.Add(mapping);
            }
        }
        else
        {
            // Single file, no chapters: treat as single track
            var singleFile = media.AudioFiles[0];
            var mapping = new TrackMapping
            {
                CardTransferId = transfer.Id,
                AbsFileIno = singleFile.Ino,
                ChapterTitle = media.Metadata.Title ?? "Track 1",
                ChapterIndex = 0,
                StartTime = 0,
                EndTime = singleFile.Duration,
                FileSizeBytes = singleFile.Size
            };
            db.TrackMappings.Add(mapping);
            mappings.Add(mapping);
        }

        await db.SaveChangesAsync(ct);
        return (mappings, chapterPaths);
    }

    internal async Task UploadTracksAsync(
        string yotoAccessToken, List<TrackMapping> mappings, Dictionary<int, string> chapterPaths,
        CardTransfer transfer, CancellationToken ct)
    {
        var user = await db.UserConnections.FindAsync([transfer.UserConnectionId], ct)!;

        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];

            // Check for existing SHA256 deduplication.
            // Scoped to the same user connection: Yoto media (yoto:#sha) is account-scoped and
            // AbsFileIno (inode) can collide across different Audiobookshelf servers.
            var existingSha = await db.TrackMappings
                .Where(tm => tm.AbsFileIno == mapping.AbsFileIno &&
                             tm.YotoTranscodedSha256 != null &&
                             tm.Id != mapping.Id &&
                             tm.CardTransfer.UserConnectionId == transfer.UserConnectionId)
                .Select(tm => tm.YotoTranscodedSha256)
                .FirstOrDefaultAsync(ct);

            if (existingSha is not null)
            {
                logger.LogInformation("Reusing existing SHA256 for track {FileIno}", mapping.AbsFileIno);
                mapping.YotoTranscodedSha256 = existingSha;
                mapping.YotoTrackUrl = $"yoto:#{existingSha}";
                continue;
            }

            // Determine source: extracted chapter file or direct download from ABS
            Stream audioStream;
            long contentLength;
            string contentType;

            if (chapterPaths.TryGetValue(mapping.ChapterIndex, out var chapterPath) && File.Exists(chapterPath))
            {
                audioStream = File.OpenRead(chapterPath);
                contentLength = new FileInfo(chapterPath).Length;
                contentType = "audio/mp4";
            }
            else
            {
                // Direct download from ABS — need to buffer to temp file for content-length
                var tempPath = Path.Combine(TempDir, $"{transfer.Id}_track{i}.tmp");
                await DownloadToFileAsync(user!, transfer.AbsLibraryItemId, mapping.AbsFileIno, tempPath, ct);
                audioStream = File.OpenRead(tempPath);
                contentLength = new FileInfo(tempPath).Length;
                contentType = "audio/mpeg";
            }

            try
            {
                var sha256 = await yotoService.UploadAndTranscodeAsync(
                    yotoAccessToken, audioStream, contentLength, contentType,
                    new Progress<int>(p =>
                    {
                        var overallProgress = 20 + (int)((i + p / 100.0) / mappings.Count * 50);
                        transfer.ProgressPercent = Math.Min(overallProgress, 70);
                        var step = p >= 60
                            ? $"Transcoding track {i + 1}/{mappings.Count} on Yoto…"
                            : $"Uploading track {i + 1}/{mappings.Count}…";
                        // Best-effort live update; no DB write from the progress callback.
                        _ = NotifyAsync(transfer, step, CancellationToken.None);
                    }),
                    ct);

                mapping.YotoTranscodedSha256 = sha256;
                mapping.YotoTrackUrl = $"yoto:#{sha256}";
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                await audioStream.DisposeAsync();
            }
        }
    }

    internal async Task<Dictionary<int, string>> GenerateIconsAsync(
        string yotoAccessToken, UserConnection user,
        AbsBookMedia media, List<TrackMapping> mappings, CancellationToken ct)
    {
        var chapterIcons = new Dictionary<int, string>();
        var primaryGenre = media.Metadata.Genres?.FirstOrDefault();
        var bookTitle = media.Metadata.Title ?? "Unknown";

        // Reuse identical icons generated earlier in this same run — a DB query can't see
        // entities added but not yet saved, so we also track them in memory here.
        var iconsByHash = new Dictionary<string, GeneratedIcon>();

        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            var prompt = iconService.BuildChapterIconPrompt(mapping.ChapterTitle, bookTitle, primaryGenre);
            var contentHash = ComputeContentHash(prompt);

            try
            {
                // Reuse an icon already produced this run for an identical prompt
                if (iconsByHash.TryGetValue(contentHash, out var runIcon))
                {
                    runIcon.TimesUsed++;
                    mapping.GeneratedIconId = runIcon.Id;
                    if (runIcon.YotoMediaId is not null) chapterIcons[i] = $"yoto:#{runIcon.YotoMediaId}";
                    continue;
                }

                // Reuse a previously persisted icon for this user — avoids burning Gemini quota
                var existing = await db.GeneratedIcons
                    .FirstOrDefaultAsync(g => g.UserConnectionId == user.Id &&
                                              g.ContentHash == contentHash &&
                                              g.YotoMediaId != null, ct);
                if (existing is not null)
                {
                    existing.TimesUsed++;
                    mapping.GeneratedIconId = existing.Id;
                    chapterIcons[i] = $"yoto:#{existing.YotoMediaId}";
                    iconsByHash[contentHash] = existing;
                    continue;
                }

                var iconBytes = await iconService.GenerateChapterIconAsync(
                    mapping.ChapterTitle, bookTitle, primaryGenre, ct);

                var iconUpload = await yotoService.UploadCustomIconAsync(
                    yotoAccessToken, iconBytes, $"ch{i}_{mapping.ChapterTitle[..Math.Min(20, mapping.ChapterTitle.Length)]}.png", ct);

                var icon = new GeneratedIcon
                {
                    UserConnectionId = user.Id,
                    Prompt = prompt,
                    ContextTitle = $"{bookTitle} - {mapping.ChapterTitle}",
                    Source = IconSource.GeminiGenerated,
                    YotoMediaId = iconUpload.MediaId,
                    YotoIconUrl = iconUpload.Url,
                    ContentHash = contentHash,
                    TimesUsed = 1
                };

                db.GeneratedIcons.Add(icon);
                mapping.GeneratedIconId = icon.Id;
                chapterIcons[i] = $"yoto:#{iconUpload.MediaId}";
                iconsByHash[contentHash] = icon;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Icon generation failed for chapter {Index}: {Title}",
                    i, mapping.ChapterTitle);
                // Leave the chapter without a custom icon — Yoto rejects any icon16x16 that isn't
                // a valid yoto:#{mediaId} reference, so a placeholder URL would fail the whole card.
            }
        }

        await db.SaveChangesAsync(ct);
        return chapterIcons;
    }

    internal async Task<string> CreateYotoCardAsync(
        string yotoAccessToken, CardTransfer transfer, AbsBookMetadata metadata,
        List<TrackMapping> mappings, Dictionary<int, string> chapterIcons, string? coverUrl,
        CancellationToken ct)
    {
        var chapters = new List<YotoChapter>();

        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            // Only set a display icon when we have a valid yoto:#{mediaId} reference.
            var iconRef = chapterIcons.GetValueOrDefault(i);
            var display = iconRef is not null ? new YotoDisplay(iconRef) : null;

            chapters.Add(new YotoChapter(
                Key: (i + 1).ToString("D2"),
                Title: mapping.ChapterTitle,
                Tracks:
                [
                    new YotoTrack(
                        Key: $"{(i + 1):D2}01",
                        Title: mapping.ChapterTitle,
                        TrackUrl: mapping.YotoTrackUrl ?? "",
                        Format: "aac",
                        Type: "audio",
                        Duration: mapping.TranscodedDuration ?? (mapping.EndTime - mapping.StartTime),
                        FileSize: mapping.TranscodedFileSize ?? mapping.FileSizeBytes,
                        Channels: "stereo",
                        Display: display
                    )
                ],
                Display: display
            ));
        }

        var content = new YotoCardContent(
            Chapters: chapters.ToArray(),
            Config: new YotoCardConfig(AllowSkip: true, AllowFastForward: true, AllowRewind: true),
            PlaybackType: transfer.PlaybackType == PlaybackType.Linear ? "linear" : "interactive",
            Version: "1"
        );

        var cardTitle = metadata.Title ?? transfer.BookTitle;
        var language = MapYotoLanguage(metadata.Language);
        var cardMetadata = new YotoCardMetadata(
            Author: metadata.Authors?.FirstOrDefault()?.Name,
            Category: transfer.Category.ToString().ToLowerInvariant(),
            Description: metadata.Description ?? metadata.Subtitle ?? cardTitle,
            Genre: metadata.Genres,
            Languages: language is not null ? [language] : null,
            MinAge: transfer.EffectiveMinAge,
            MaxAge: transfer.EffectiveMaxAge,
            ReadBy: metadata.Narrators?.FirstOrDefault(),
            Cover: coverUrl is not null ? new YotoCover(coverUrl) : null
        );

        // Check if updating existing card
        var existingCardId = transfer.YotoCardId;
        if (existingCardId is null)
        {
            var existingTransfer = await db.CardTransfers
                .Where(t => t.AbsLibraryItemId == transfer.AbsLibraryItemId &&
                            t.YotoCardId != null &&
                            t.Id != transfer.Id)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            existingCardId = existingTransfer?.YotoCardId;
        }

        return await yotoService.CreateOrUpdateCardAsync(
            yotoAccessToken, content, cardMetadata, title: cardTitle, existingCardId: existingCardId, ct: ct);
    }

    // =========================================================================
    // Helper methods
    // =========================================================================

    private static void EnsureValidConnections(UserConnection user)
    {
        if (!user.HasValidAbsConnection)
            throw new InvalidOperationException("No valid Audiobookshelf connection");
        if (!user.HasValidYotoConnection)
            throw new InvalidOperationException("No valid Yoto connection");
    }

    private Task<string> EnsureYotoTokenAsync(UserConnection user, CancellationToken ct) =>
        YotoTokens.EnsureValidAsync(db, yotoService, user, logger, ct);

    private async Task DownloadToFileAsync(
        UserConnection user, string itemId, string fileIno, string outputPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await using var sourceStream = await absService.DownloadAudioFileAsync(
            user.AudiobookshelfUrl, user.AudiobookshelfToken!, itemId, fileIno, ct);
        await using var fileStream = File.Create(outputPath);
        await sourceStream.CopyToAsync(fileStream, ct);

        var fileSize = fileStream.Length;
        logger.LogInformation("Downloaded {FileIno} to {Path} ({Size} bytes)",
            fileIno, outputPath, fileSize);
    }

    private async Task UpdateStatus(
        CardTransfer transfer, TransferStatus status, int progress, CancellationToken ct)
    {
        // Re-read from DB to detect if transfer was cancelled while we were working
        await db.Entry(transfer).ReloadAsync(ct);
        if (transfer.Status == TransferStatus.Cancelled)
            throw new OperationCanceledException("Transfer was cancelled");

        transfer.Status = status;
        transfer.ProgressPercent = progress;
        await db.SaveChangesAsync(ct);

        await NotifyAsync(transfer, StepLabel(status), ct);
    }

    private async Task NotifyAsync(CardTransfer transfer, string step, CancellationToken ct)
    {
        try
        {
            await notifier.SendProgressAsync(
                new TransferProgressUpdate(transfer.Id, transfer.Status, transfer.ProgressPercent, step, transfer.ErrorMessage),
                ct);
        }
        catch (Exception ex)
        {
            // Progress is best-effort; never fail a transfer because a notification didn't send.
            logger.LogDebug(ex, "Progress notification failed for transfer {TransferId}", transfer.Id);
        }
    }

    private static string StepLabel(TransferStatus status) => status switch
    {
        TransferStatus.DownloadingAudio => "Downloading audio",
        TransferStatus.UploadingToYoto => "Uploading & transcoding on Yoto",
        TransferStatus.GeneratingIcons => "Generating chapter icons",
        TransferStatus.CreatingCard => "Creating Yoto card",
        TransferStatus.Completed => "Transfer complete",
        TransferStatus.Failed => "Transfer failed",
        TransferStatus.Cancelled => "Transfer cancelled",
        _ => status.ToString()
    };

    private void CleanupTempFiles(Guid transferId)
    {
        try
        {
            // Stryker disable once Statement : without the guard GetFiles throws and the catch below logs a warning; nothing else differs
            if (!Directory.Exists(TempDir)) return;

            var files = Directory.GetFiles(TempDir, $"{transferId}*");
            foreach (var file in files)
            {
                File.Delete(file);
                logger.LogDebug("Cleaned up temp file: {File}", file);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up temp files for transfer {TransferId}", transferId);
        }
    }

    private static string ComputeContentHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Maps an Audiobookshelf language value to a Yoto-accepted code, or null to omit it.
    /// Yoto only accepts: en, en-gb, en-us, fr, fr-fr, es, es-es, es-419, de, it, zh_Hans.
    /// </summary>
    private static string? MapYotoLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var lower = language.Trim().ToLowerInvariant();

        switch (lower)
        {
            case "en":
            case "en-gb":
            case "en-us":
            case "fr":
            case "fr-fr":
            case "es":
            case "es-es":
            case "es-419":
            case "de":
            case "it":
                return lower;
            case "zh_hans":
            case "zh-hans":
            case "zh":
                return "zh_Hans";
        }

        if (lower.StartsWith("en") || lower.Contains("english")) return "en";
        if (lower.StartsWith("fr") || lower.Contains("french")) return "fr";
        if (lower.StartsWith("es") || lower.StartsWith("spa") || lower.Contains("spanish")) return "es";
        if (lower.StartsWith("de") || lower.StartsWith("ger") || lower.StartsWith("deu") || lower.Contains("german")) return "de";
        if (lower.StartsWith("it") || lower.Contains("italian")) return "it";
        if (lower.StartsWith("zh") || lower.Contains("chinese")) return "zh_Hans";
        return null;
    }

    internal static float? ParseSequence(string? sequence)
    {
        if (string.IsNullOrEmpty(sequence)) return null;
        return float.TryParse(sequence, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static TransferResponse MapToResponse(CardTransfer t) => new(
        t.Id,
        t.AbsLibraryItemId,
        t.BookTitle,
        t.BookAuthor,
        t.SeriesName,
        t.SeriesSequence,
        t.Status,
        t.ProgressPercent,
        t.ErrorMessage,
        new AgeRangeResponse(
            t.SuggestedMinAge, t.SuggestedMaxAge,
            t.AgeSuggestionReason, t.AgeSuggestionSource,
            t.OverrideMinAge, t.OverrideMaxAge,
            t.EffectiveMinAge, t.EffectiveMaxAge),
        t.YotoCardId,
        t.CreatedAt,
        t.CompletedAt,
        t.TrackMappings.Select(tm => new TrackMappingResponse(
            tm.Id, tm.ChapterTitle, tm.ChapterIndex,
            tm.EndTime - tm.StartTime, tm.IsUploaded,
            tm.GeneratedIcon?.YotoIconUrl)).ToArray()
    );
}
