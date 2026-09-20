using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Services.BackgroundJobs;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AudioYotoShelf.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TransfersController(
    AudioYotoShelfDbContext db,
    ITransferOrchestrator orchestrator,
    IBackgroundJobClient backgroundJobs,
    ITransferProgressNotifier notifier,
    ILogger<TransfersController> logger) : AppControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTransfers(
        [FromQuery] int page = 0, [FromQuery] int limit = 20,
        [FromQuery] TransferStatus? status = null,
        CancellationToken ct = default)
    {
        var userConnectionId = CurrentUserConnectionId;
        var query = db.CardTransfers
            .Where(t => t.UserConnectionId == userConnectionId)
            .Include(t => t.TrackMappings)
                .ThenInclude(tm => tm.GeneratedIcon)
            .OrderByDescending(t => t.CreatedAt)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        var total = await query.CountAsync(ct);
        var transfers = await query.Skip(page * limit).Take(limit).ToArrayAsync(ct);

        return Ok(new
        {
            Results = transfers.Select(MapToResponse).ToArray(),
            Total = total,
            Page = page,
            Limit = limit
        });
    }

    [HttpGet("detail/{transferId:guid}")]
    public async Task<IActionResult> GetTransfer(Guid transferId, CancellationToken ct)
    {
        var transfer = await db.CardTransfers
            .Include(t => t.TrackMappings)
                .ThenInclude(tm => tm.GeneratedIcon)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        // Treat "not yours" as "not found" so a transfer id can't be probed across accounts.
        if (transfer is null || transfer.UserConnectionId != CurrentUserConnectionId)
            return NotFound();
        return Ok(MapToResponse(transfer));
    }

    [HttpPost("book")]
    public async Task<IActionResult> TransferBook(
        [FromBody] CreateTransferRequest request,
        CancellationToken ct)
    {
        var userConnectionId = CurrentUserConnectionId;

        // Guard against duplicate transfers for the same item
        var hasActive = await db.CardTransfers.AnyAsync(
            t => t.UserConnectionId == userConnectionId
                 && t.AbsLibraryItemId == request.AbsLibraryItemId
                 && (t.Status == Core.Enums.TransferStatus.Pending
                     || t.Status == Core.Enums.TransferStatus.DownloadingAudio
                     || t.Status == Core.Enums.TransferStatus.UploadingToYoto
                     || t.Status == Core.Enums.TransferStatus.AwaitingTranscode
                     || t.Status == Core.Enums.TransferStatus.GeneratingIcons
                     || t.Status == Core.Enums.TransferStatus.CreatingCard), ct);

        if (hasActive)
            return Conflict(new { Message = "A transfer is already in progress for this book" });

        var transferId = Guid.NewGuid();
        var jobId = backgroundJobs.Enqueue<ITransferJobService>(
            svc => svc.ExecuteBookTransferAsync(userConnectionId, request, transferId, CancellationToken.None));

        logger.LogInformation("Book transfer queued: {ItemId} → Transfer {TransferId}, Job {JobId}",
            request.AbsLibraryItemId, transferId, jobId);

        await notifier.NotifyListChangedAsync(ct);
        return Accepted(new { TransferId = transferId, JobId = jobId, Message = "Transfer queued" });
    }

    [HttpPost("series")]
    public async Task<IActionResult> TransferSeries(
        [FromBody] CreateSeriesTransferRequest request,
        CancellationToken ct)
    {
        var jobId = backgroundJobs.Enqueue<ITransferJobService>(
            svc => svc.ExecuteSeriesTransferAsync(CurrentUserConnectionId, request, CancellationToken.None));

        logger.LogInformation("Series transfer queued: {SeriesId} → Job {JobId}",
            request.AbsSeriesId, jobId);

        await notifier.NotifyListChangedAsync(ct);
        return Accepted(new { JobId = jobId, Message = "Series transfer queued" });
    }

    /// <summary>
    /// Phase 2: Batch transfer — enqueues one Hangfire job per book, returns batch summary.
    /// ISP: BatchTransferRequest is its own DTO, not overloading CreateTransferRequest.
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> TransferBatch(
        [FromBody] BatchTransferRequest request,
        CancellationToken ct)
    {
        var userConnectionId = CurrentUserConnectionId;
        var jobIds = new List<string>();
        foreach (var itemId in request.AbsLibraryItemIds)
        {
            var bookRequest = new CreateTransferRequest(
                AbsLibraryItemId: itemId,
                Category: request.Category,
                PlaybackType: request.PlaybackType,
                OverrideMinAge: request.OverrideMinAge,
                OverrideMaxAge: request.OverrideMaxAge
            );
            var transferId = Guid.NewGuid();
            var jobId = backgroundJobs.Enqueue<ITransferJobService>(
                svc => svc.ExecuteBookTransferAsync(userConnectionId, bookRequest, transferId, CancellationToken.None));
            jobIds.Add(jobId);
        }

        logger.LogInformation("Batch transfer queued: {Count} books → {JobCount} jobs",
            request.AbsLibraryItemIds.Length, jobIds.Count);

        var batchId = Guid.NewGuid().ToString("N")[..12];
        await notifier.NotifyListChangedAsync(ct);
        return Accepted(new BatchTransferResponse(batchId, request.AbsLibraryItemIds.Length, jobIds.Count, jobIds.ToArray()));
    }

    [HttpPost("retry/{transferId:guid}")]
    public async Task<IActionResult> RetryTransfer(Guid transferId, CancellationToken ct)
    {
        if (!await OwnsTransferAsync(transferId, ct)) return NotFound();

        var jobId = backgroundJobs.Enqueue<ITransferJobService>(
            svc => svc.ExecuteRetryTransferAsync(transferId, CancellationToken.None));

        return Accepted(new { JobId = jobId, Message = "Retry queued" });
    }

    [HttpPost("cancel/{transferId:guid}")]
    public async Task<IActionResult> CancelTransfer(Guid transferId, CancellationToken ct)
    {
        if (!await OwnsTransferAsync(transferId, ct)) return NotFound();

        await orchestrator.CancelTransferAsync(transferId, ct);
        return Ok(new { Message = "Transfer cancelled" });
    }

    [HttpDelete("{transferId:guid}")]
    public async Task<IActionResult> DeleteTransfer(Guid transferId, CancellationToken ct)
    {
        var transfer = await db.CardTransfers
            .Include(t => t.TrackMappings)
            .FirstOrDefaultAsync(t => t.Id == transferId, ct);

        if (transfer is null || transfer.UserConnectionId != CurrentUserConnectionId)
            return NotFound();

        if (transfer.Status is not (Core.Enums.TransferStatus.Completed
            or Core.Enums.TransferStatus.Failed
            or Core.Enums.TransferStatus.Cancelled))
            return Conflict(new { Message = "Can only delete completed, failed, or cancelled transfers" });

        // Stryker disable once Statement : redundant with the cascade in TrackMappingConfiguration; kept explicit so deletion does not depend on it
        db.TrackMappings.RemoveRange(transfer.TrackMappings);
        db.CardTransfers.Remove(transfer);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted transfer {TransferId} ({BookTitle})", transferId, transfer.BookTitle);
        return Ok(new { Message = "Transfer deleted" });
    }

    /// <summary>Clears (removes the records for) all completed transfers for a user. Yoto cards are untouched.</summary>
    [HttpDelete("completed")]
    public async Task<IActionResult> ClearCompleted(CancellationToken ct)
    {
        var userConnectionId = CurrentUserConnectionId;
        var completed = await db.CardTransfers
            .Where(t => t.UserConnectionId == userConnectionId && t.Status == Core.Enums.TransferStatus.Completed)
            .Include(t => t.TrackMappings)
            .ToListAsync(ct);

        // Stryker disable once Statement : redundant with the cascade in TrackMappingConfiguration; kept explicit so deletion does not depend on it
        foreach (var transfer in completed)
            db.TrackMappings.RemoveRange(transfer.TrackMappings);
        db.CardTransfers.RemoveRange(completed);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Cleared {Count} completed transfers for user {UserId}", completed.Count, userConnectionId);
        return Ok(new { Cleared = completed.Count });
    }

    /// <summary>True when the transfer exists and belongs to the authenticated connection.</summary>
    private async Task<bool> OwnsTransferAsync(Guid transferId, CancellationToken ct)
    {
        var owner = await db.CardTransfers
            .Where(t => t.Id == transferId)
            .Select(t => (Guid?)t.UserConnectionId)
            .FirstOrDefaultAsync(ct);
        return owner is not null && owner == CurrentUserConnectionId;
    }

    private static TransferResponse MapToResponse(Core.Entities.CardTransfer t) => new(
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
