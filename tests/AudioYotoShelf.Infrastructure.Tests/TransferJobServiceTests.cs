using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services.BackgroundJobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class TransferJobServiceTests
{
    private readonly Mock<ITransferOrchestrator> _orchestrator;
    private readonly Mock<ITransferProgressNotifier> _notifier;
    private readonly TransferJobService _sut;

    public TransferJobServiceTests()
    {
        _orchestrator = new Mock<ITransferOrchestrator>();
        _notifier = new Mock<ITransferProgressNotifier>();

        _sut = new TransferJobService(
            _orchestrator.Object, _notifier.Object,
            Mock.Of<ILogger<TransferJobService>>());
    }

    // =========================================================================
    // ExecuteBookTransferAsync
    // =========================================================================

    [Fact]
    public async Task ExecuteBookTransferAsync_CallsOrchestrator()
    {
        var userId = Guid.NewGuid();
        var request = TestData.CreateTransferRequest();
        var response = new TransferResponse(
            Guid.NewGuid(), "item-1", "Test Book", "Author", null, null,
            TransferStatus.Completed, 100, null,
            new AgeRangeResponse(5, 10, "Test", AgeRangeSource.Default, null, null, 5, 10),
            "card-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        _orchestrator.Setup(o => o.TransferBookAsync(userId, request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        await _sut.ExecuteBookTransferAsync(userId, request, null, CancellationToken.None);

        _orchestrator.Verify(o => o.TransferBookAsync(userId, request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteBookTransferAsync_SendsProgressOnComplete()
    {
        var userId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var response = new TransferResponse(
            transferId, "item-1", "Test Book", "Author", null, null,
            TransferStatus.Completed, 100, null,
            new AgeRangeResponse(5, 10, "Test", AgeRangeSource.Default, null, null, 5, 10),
            "card-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        _orchestrator.Setup(o => o.TransferBookAsync(
                It.IsAny<Guid>(), It.IsAny<CreateTransferRequest>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        await _sut.ExecuteBookTransferAsync(userId, TestData.CreateTransferRequest(), null, CancellationToken.None);

        _notifier.Verify(n => n.SendProgressAsync(
            It.Is<TransferProgressUpdate>(u => u.TransferId == transferId && u.Status == TransferStatus.Completed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteBookTransferAsync_OrchestratorThrows_PropagatesException()
    {
        _orchestrator.Setup(o => o.TransferBookAsync(
                It.IsAny<Guid>(), It.IsAny<CreateTransferRequest>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("External failure"));

        var act = () => _sut.ExecuteBookTransferAsync(
            Guid.NewGuid(), TestData.CreateTransferRequest(), null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // =========================================================================
    // ExecuteSeriesTransferAsync
    // =========================================================================

    [Fact]
    public async Task ExecuteSeriesTransferAsync_CallsOrchestrator()
    {
        var userId = Guid.NewGuid();
        var request = TestData.CreateSeriesTransferRequest();

        _orchestrator.Setup(o => o.TransferSeriesAsync(
                userId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.ExecuteSeriesTransferAsync(userId, request, CancellationToken.None);

        _orchestrator.Verify(o => o.TransferSeriesAsync(userId, request, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =========================================================================
    // ExecuteRetryTransferAsync
    // =========================================================================

    [Fact]
    public async Task ExecuteRetryTransferAsync_CallsOrchestrator()
    {
        var transferId = Guid.NewGuid();
        var response = new TransferResponse(
            transferId, "item-1", "Test Book", "Author", null, null,
            TransferStatus.Completed, 100, null,
            new AgeRangeResponse(5, 10, "Test", AgeRangeSource.Default, null, null, 5, 10),
            "card-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        _orchestrator.Setup(o => o.RetryTransferAsync(transferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        await _sut.ExecuteRetryTransferAsync(transferId, CancellationToken.None);

        _orchestrator.Verify(o => o.RetryTransferAsync(transferId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =========================================================================
    // Mutation-testing additions
    // =========================================================================
    // A job that swallows its exception is reported to Hangfire as a success: no failure state, no retry.

    private static TransferResponse ResponseWith(Guid id, TransferStatus status, int progress, string? error) =>
        new(id, "item-1", "Test Book", "Author", null, null, status, progress, error,
            new AgeRangeResponse(5, 10, "Test", AgeRangeSource.Default, null, null, 5, 10),
            "card-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

    [Fact]
    public async Task ExecuteSeriesTransferAsync_ReportsEachBooksOwnStatusProgressAndError()
    {
        var done = ResponseWith(Guid.NewGuid(), TransferStatus.Completed, 100, null);
        var failed = ResponseWith(Guid.NewGuid(), TransferStatus.Failed, 40, "upload failed");
        _orchestrator.Setup(o => o.TransferSeriesAsync(It.IsAny<Guid>(), It.IsAny<CreateSeriesTransferRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([done, failed]);

        await _sut.ExecuteSeriesTransferAsync(Guid.NewGuid(), TestData.CreateSeriesTransferRequest(), CancellationToken.None);

        _notifier.Verify(n => n.SendProgressAsync(
            It.Is<TransferProgressUpdate>(u => u.TransferId == done.Id && u.Status == TransferStatus.Completed
                && u.ProgressPercent == 100 && u.ErrorMessage == null), It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.SendProgressAsync(
            It.Is<TransferProgressUpdate>(u => u.TransferId == failed.Id && u.Status == TransferStatus.Failed
                && u.ProgressPercent == 40 && u.ErrorMessage == "upload failed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteSeriesTransferAsync_OrchestratorThrows_PropagatesException()
    {
        _orchestrator.Setup(o => o.TransferSeriesAsync(It.IsAny<Guid>(), It.IsAny<CreateSeriesTransferRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("series boom"));

        var act = () => _sut.ExecuteSeriesTransferAsync(Guid.NewGuid(), TestData.CreateSeriesTransferRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("series boom");
    }

    [Fact]
    public async Task ExecuteRetryTransferAsync_ReportsTheRetriedTransferAsCompleted()
    {
        var transferId = Guid.NewGuid();
        _orchestrator.Setup(o => o.RetryTransferAsync(transferId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResponseWith(transferId, TransferStatus.Completed, 100, null));

        await _sut.ExecuteRetryTransferAsync(transferId, CancellationToken.None);

        _notifier.Verify(n => n.SendProgressAsync(
            It.Is<TransferProgressUpdate>(u => u.TransferId == transferId && u.Status == TransferStatus.Completed
                && u.ProgressPercent == 100 && u.ErrorMessage == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteRetryTransferAsync_OrchestratorThrows_PropagatesException()
    {
        _orchestrator.Setup(o => o.RetryTransferAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("retry boom"));

        var act = () => _sut.ExecuteRetryTransferAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("retry boom");
    }

    // --- PlaylistJobService

    [Fact]
    public async Task ExecutePlaylistTransferAsync_CallsTheOrchestratorForThatPlaylist()
    {
        var orchestrator = new Mock<IPlaylistTransferOrchestrator>();
        var playlistId = Guid.NewGuid();

        await new PlaylistJobService(orchestrator.Object, Mock.Of<ILogger<PlaylistJobService>>())
            .ExecutePlaylistTransferAsync(playlistId, CancellationToken.None);

        orchestrator.Verify(o => o.TransferPlaylistAsync(playlistId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecutePlaylistTransferAsync_OrchestratorThrows_PropagatesException()
    {
        var orchestrator = new Mock<IPlaylistTransferOrchestrator>();
        orchestrator.Setup(o => o.TransferPlaylistAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("playlist boom"));

        var act = () => new PlaylistJobService(orchestrator.Object, Mock.Of<ILogger<PlaylistJobService>>())
            .ExecutePlaylistTransferAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("playlist boom");
    }

    // --- Registration

    [Fact]
    public void AddTransferJobs_RegistersBothJobServicesAsScoped()
    {
        var services = new ServiceCollection().AddTransferJobs();

        services.Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime)).Should().BeEquivalentTo(
        [
            (typeof(ITransferJobService), typeof(TransferJobService), ServiceLifetime.Scoped),
            (typeof(IPlaylistJobService), typeof(PlaylistJobService), ServiceLifetime.Scoped),
        ]);
    }
}
