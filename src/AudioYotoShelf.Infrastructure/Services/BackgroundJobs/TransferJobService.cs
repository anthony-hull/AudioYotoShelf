using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services.BackgroundJobs;

/// <summary>
/// Hangfire job definitions for audio transfer processing.
/// Each method is designed to be called by Hangfire's background job infrastructure.
/// </summary>
public interface ITransferJobService
{
    [Queue("transfers")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ExecuteBookTransferAsync(Guid userConnectionId, CreateTransferRequest request, Guid? transferId, CancellationToken ct);

    [Queue("transfers")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ExecuteSeriesTransferAsync(Guid userConnectionId, CreateSeriesTransferRequest request, CancellationToken ct);

    [Queue("transfers")]
    [AutomaticRetry(Attempts = 2, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ExecuteRetryTransferAsync(Guid transferId, CancellationToken ct);
}

public class TransferJobService(
        ITransferOrchestrator orchestrator,
        ITransferProgressNotifier notifier,
        ILogger<TransferJobService> logger) : ITransferJobService
{
    public async Task ExecuteBookTransferAsync(
            Guid userConnectionId, CreateTransferRequest request, Guid? transferId, CancellationToken ct)
    {
        logger.LogInformation("Hangfire: Starting book transfer job for item {ItemId}",
                request.AbsLibraryItemId);

        try
        {
            var result = await orchestrator.TransferBookAsync(userConnectionId, request, transferId, ct);

            await notifier.SendProgressAsync(new TransferProgressUpdate(
                    result.Id,
                    TransferStatus.Completed,
                    100,
                    "Transfer complete",
                    null
            ), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The person pressed Cancel, which is the transfer ending as asked. Rethrowing would make
            // Hangfire count a failure and retry it, restarting what they just cancelled. A cancelled
            // token is different (the server is stopping) and does propagate, so the job is requeued.
            logger.LogInformation("Hangfire: Book transfer for item {ItemId} was cancelled", request.AbsLibraryItemId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hangfire: Book transfer failed for item {ItemId}",
                    request.AbsLibraryItemId);
            throw;
        }
    }

    public async Task ExecuteSeriesTransferAsync(
            Guid userConnectionId, CreateSeriesTransferRequest request, CancellationToken ct)
    {
        logger.LogInformation("Hangfire: Starting series transfer job for series {SeriesId}",
                request.AbsSeriesId);

        try
        {
            var results = await orchestrator.TransferSeriesAsync(userConnectionId, request, ct);

            foreach (var result in results)
            {
                await notifier.SendProgressAsync(new TransferProgressUpdate(
                        result.Id,
                        result.Status,
                        result.ProgressPercent,
                        "Series transfer complete",
                        result.ErrorMessage
                ), ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Hangfire: Series transfer for {SeriesId} was cancelled", request.AbsSeriesId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hangfire: Series transfer failed for {SeriesId}",
                    request.AbsSeriesId);
            throw;
        }
    }

    public async Task ExecuteRetryTransferAsync(Guid transferId, CancellationToken ct)
    {
        logger.LogInformation("Hangfire: Retrying transfer {TransferId}", transferId);

        try
        {
            var result = await orchestrator.RetryTransferAsync(transferId, ct);

            await notifier.SendProgressAsync(new TransferProgressUpdate(
                    result.Id,
                    TransferStatus.Completed,
                    100,
                    "Retry complete",
                    null
            ), ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Hangfire: Retry of transfer {TransferId} was cancelled", transferId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hangfire: Retry failed for transfer {TransferId}", transferId);
            throw;
        }
    }
}

/// <summary>
/// Hangfire job for transferring an entire playlist to a single Yoto card.
/// </summary>
public interface IPlaylistJobService
{
    [Queue("transfers")]
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ExecutePlaylistTransferAsync(Guid playlistId, CancellationToken ct);
}

public class PlaylistJobService(
        IPlaylistTransferOrchestrator orchestrator,
        ILogger<PlaylistJobService> logger) : IPlaylistJobService
{
    public async Task ExecutePlaylistTransferAsync(Guid playlistId, CancellationToken ct)
    {
        logger.LogInformation("Hangfire: Starting playlist transfer {PlaylistId}", playlistId);
        try
        {
            await orchestrator.TransferPlaylistAsync(playlistId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hangfire: Playlist transfer failed {PlaylistId}", playlistId);
            throw;
        }
    }
}

/// <summary>
/// Extension methods for registering Hangfire jobs in Program.cs.
/// </summary>
public static class HangfireJobRegistration
{
    public static IServiceCollection AddTransferJobs(this IServiceCollection services)
    {
        services.AddScoped<ITransferJobService, TransferJobService>();
        services.AddScoped<IPlaylistJobService, PlaylistJobService>();
        return services;
    }
}
