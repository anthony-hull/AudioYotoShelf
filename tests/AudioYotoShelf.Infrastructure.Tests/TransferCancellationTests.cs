using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Observability;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

/// <summary>
/// Cancel only sets a flag on the transfer. The running job has to look for it, and it used to look
/// only when the status next changed, so a cancelled 17-track book kept going for the best part of an hour.
/// </summary>
public class TransferCancellationTests : IDisposable
{
    private const string Token = "yoto-token";

    private readonly InMemoryDbFixture _dbFixture = new();
    private readonly Mock<IYotoService> _yoto = new();
    private readonly Mock<IAudiobookshelfService> _abs = new();
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"cancel-{Guid.NewGuid():N}");
    private readonly TransferOrchestrator _sut;

    public TransferCancellationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = _tempDirectory })
            .Build();
        _sut = new TransferOrchestrator(
            _dbFixture.DbContext, _abs.Object, _yoto.Object, Mock.Of<IIconGenerationService>(),
            Mock.Of<IAgeSuggestionService>(), Mock.Of<IChapterExtractor>(), Mock.Of<ITransferProgressNotifier>(), configuration,
            new TransferMetrics(), Mock.Of<ILogger<TransferOrchestrator>>());

        _abs.Setup(a => a.DownloadAudioFileWithMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (new MemoryStream([1, 2, 3]) as Stream, 3L, "audio/mpeg"));
    }

    public void Dispose()
    {
        _dbFixture.Dispose();
        if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, recursive: true);
    }

    private async Task<(CardTransfer Transfer, List<TrackMapping> Tracks)> SeedTransferAsync(int trackCount)
    {
        var db = _dbFixture.DbContext;
        var user = TestData.CreateUserConnection();
        db.UserConnections.Add(user);
        var transfer = new CardTransfer
        {
            UserConnectionId = user.Id,
            AbsLibraryItemId = "item-1",
            BookTitle = "Book",
            AgeSuggestionReason = "n/a",
        };
        db.CardTransfers.Add(transfer);
        var tracks = Enumerable.Range(0, trackCount).Select(i => new TrackMapping
        {
            CardTransferId = transfer.Id,
            AbsFileIno = $"ino-{i}",
            ChapterTitle = $"Chapter {i + 1}",
            ChapterIndex = i,
        }).ToList();
        db.TrackMappings.AddRange(tracks);
        await db.SaveChangesAsync();
        return (transfer, tracks);
    }

    private async Task CancelAsync(CardTransfer transfer)
    {
        transfer.Status = TransferStatus.Cancelled;
        await _dbFixture.DbContext.SaveChangesAsync();
    }

    private int YotoUploads() => _yoto.Invocations.Count(i => i.Method.Name == nameof(IYotoService.UploadAndTranscodeAsync));

    [Fact]
    public async Task ACancelDuringOneTrack_StopsTheTransferBeforeTheNextTrackStarts()
    {
        var (transfer, tracks) = await SeedTransferAsync(3);
        _yoto.Setup(y => y.UploadAndTranscodeAsync(
                Token, It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await CancelAsync(transfer); // the person presses Cancel while track 1 is with Yoto
                return "sha";
            });

        var act = () => _sut.UploadTracksAsync(Token, tracks, [], transfer, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        YotoUploads().Should().Be(1);
    }

    [Fact]
    public async Task ATransferCancelledBeforeAnyTrackStarts_DownloadsAndUploadsNothing()
    {
        var (transfer, tracks) = await SeedTransferAsync(2);
        await CancelAsync(transfer);

        var act = () => _sut.UploadTracksAsync(Token, tracks, [], transfer, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        YotoUploads().Should().Be(0);
        _abs.Verify(a => a.DownloadAudioFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ATransferThatIsNotCancelled_UploadsEveryTrack()
    {
        var (transfer, tracks) = await SeedTransferAsync(3);
        _yoto.Setup(y => y.UploadAndTranscodeAsync(
                Token, It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha");

        await _sut.UploadTracksAsync(Token, tracks, [], transfer, CancellationToken.None);

        YotoUploads().Should().Be(3);
    }

    [Fact]
    public async Task ATrackAlreadyOnYoto_IsStillSkippedWhenNotCancelled_WithoutRefusingTheRestOfTheTransfer()
    {
        // Guards the check itself: looking for a cancel between tracks must not read as one when there is none.
        var (transfer, tracks) = await SeedTransferAsync(2);
        tracks[0].YotoTranscodedSha256 = "already";
        await _dbFixture.DbContext.SaveChangesAsync();
        _yoto.Setup(y => y.UploadAndTranscodeAsync(
                Token, It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha");

        await _sut.UploadTracksAsync(Token, tracks, [], transfer, CancellationToken.None);

        transfer.Status.Should().NotBe(TransferStatus.Cancelled);
    }
}
