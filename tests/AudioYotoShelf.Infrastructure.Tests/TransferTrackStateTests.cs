using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.DTOs.Yoto;
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
/// The transfers screen shows each track's own state, so the server must say which track an update
/// is about and what stage it is at, not only how far the whole transfer has got.
/// </summary>
public class TransferTrackStateTests : IDisposable
{
    private const string Token = "yoto-token";

    private readonly InMemoryDbFixture _dbFixture = new();
    private readonly Mock<IYotoService> _yoto = new();
    private readonly Mock<IAudiobookshelfService> _abs = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"track-state-{Guid.NewGuid():N}");
    private readonly TransferOrchestrator _sut;

    public TransferTrackStateTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = _tempDirectory })
            .Build();
        _sut = new TransferOrchestrator(
            _dbFixture.DbContext, _abs.Object, _yoto.Object, Mock.Of<IIconGenerationService>(),
            Mock.Of<IAgeSuggestionService>(), Mock.Of<IChapterExtractor>(), _notifier, configuration,
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

    // What the fake Yoto reports for one track, as the real service does: upload steps, then Yoto's own transcode.
    private void YotoReports(params int[] trackProgress) =>
        _yoto.Setup(y => y.UploadAndTranscodeAsync(
                Token, It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, long, string, IProgress<int>?, CancellationToken>((_, _, _, _, progress, _) =>
            {
                foreach (var p in trackProgress) progress?.Report(p);
            })
            .ReturnsAsync(new YotoTranscodeResult("sha-from-yoto", null, null, null));

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

    private Task UploadAsync(CardTransfer transfer, List<TrackMapping> tracks, Dictionary<int, string>? chapterPaths = null) =>
        _sut.UploadTracksAsync(Token, tracks, chapterPaths ?? [], transfer, CancellationToken.None);

    private List<(TrackPhase? Phase, int? Percent)> ReportedFor(TrackMapping track) =>
        _notifier.Updates.Where(u => u.TrackId == track.Id).Select(u => (u.TrackPhase, u.TrackPercent)).ToList();

    [Fact]
    public async Task ATrack_IsReportedThroughEachStageInOrder()
    {
        var (transfer, tracks) = await SeedTransferAsync(1);
        YotoReports(10, 30, 60, 80, 100);

        await UploadAsync(transfer, tracks);

        ReportedFor(tracks[0]).Should().Equal(
            (TrackPhase.Downloading, null),
            (TrackPhase.Uploading, null),
            (TrackPhase.Transcoding, 0),
            (TrackPhase.Transcoding, 50),
            (TrackPhase.Transcoding, 100),
            (TrackPhase.Uploaded, null));
    }

    [Fact]
    public async Task ATrackTakenFromAnExtractedChapterFile_IsNotSaidToBeDownloading()
    {
        var (transfer, tracks) = await SeedTransferAsync(1);
        Directory.CreateDirectory(_tempDirectory);
        var chapterFile = Path.Combine(_tempDirectory, "chapter.m4a");
        await File.WriteAllBytesAsync(chapterFile, [1, 2, 3]);
        YotoReports(10, 60, 100);

        await UploadAsync(transfer, tracks, new Dictionary<int, string> { [0] = chapterFile });

        ReportedFor(tracks[0]).Select(r => r.Phase).Should().NotContain(TrackPhase.Downloading);
        ReportedFor(tracks[0]).First().Phase.Should().Be(TrackPhase.Uploading);
    }

    [Fact]
    public async Task ATrackAlreadyOnYoto_IsSaidToBeReused_WithoutContactingYoto()
    {
        var (transfer, tracks) = await SeedTransferAsync(1);
        var earlier = new CardTransfer
        {
            UserConnectionId = transfer.UserConnectionId,
            AbsLibraryItemId = "item-1",
            BookTitle = "Earlier",
            AgeSuggestionReason = "n/a",
        };
        _dbFixture.DbContext.CardTransfers.Add(earlier);
        _dbFixture.DbContext.TrackMappings.Add(new TrackMapping
        {
            CardTransferId = earlier.Id,
            AbsFileIno = "ino-0",
            ChapterTitle = "Chapter 1",
            YotoTranscodedSha256 = "already-there",
        });
        await _dbFixture.DbContext.SaveChangesAsync();

        await UploadAsync(transfer, tracks);

        ReportedFor(tracks[0]).Should().Equal((TrackPhase.Reused, null));
        _yoto.Verify(y => y.UploadAndTranscodeAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EachTrackAlreadyOnYoto_IsNamedByItsOwnPlaceInTheBook()
    {
        var (transfer, tracks) = await SeedTransferAsync(2);
        var earlier = new CardTransfer
        {
            UserConnectionId = transfer.UserConnectionId,
            AbsLibraryItemId = "item-1",
            BookTitle = "Earlier",
            AgeSuggestionReason = "n/a",
        };
        _dbFixture.DbContext.CardTransfers.Add(earlier);
        _dbFixture.DbContext.TrackMappings.AddRange(
            new TrackMapping { CardTransferId = earlier.Id, AbsFileIno = "ino-0", ChapterTitle = "One", YotoTranscodedSha256 = "a" },
            new TrackMapping { CardTransferId = earlier.Id, AbsFileIno = "ino-1", ChapterTitle = "Two", YotoTranscodedSha256 = "b" });
        await _dbFixture.DbContext.SaveChangesAsync();

        await UploadAsync(transfer, tracks);

        _notifier.Updates.Where(u => u.TrackPhase == TrackPhase.Reused).Select(u => u.CurrentStep)
            .Should().Equal("Track 1/2 is already on Yoto", "Track 2/2 is already on Yoto");
    }

    [Fact]
    public async Task ATrackThatFails_IsNeverReportedAsUploaded_AndTheOnesBeforeItAre()
    {
        var (transfer, tracks) = await SeedTransferAsync(3);
        var calls = 0;
        _yoto.Setup(y => y.UploadAndTranscodeAsync(
                Token, It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls == 2 ? throw new HttpRequestException("403") : new YotoTranscodeResult("sha", null, null, null));

        var act = () => UploadAsync(transfer, tracks);

        await act.Should().ThrowAsync<HttpRequestException>();
        ReportedFor(tracks[0]).Last().Phase.Should().Be(TrackPhase.Uploaded);
        ReportedFor(tracks[1]).Select(r => r.Phase).Should().NotContain(TrackPhase.Uploaded);
        ReportedFor(tracks[2]).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryTrackUpdate_KeepsTheOverallStatusAndProgressAlongside()
    {
        var (transfer, tracks) = await SeedTransferAsync(1);
        YotoReports(60, 100);

        await UploadAsync(transfer, tracks);

        _notifier.Updates.Where(u => u.TrackId is not null).Should().OnlyContain(u => u.TransferId == transfer.Id);
    }

    private sealed class RecordingNotifier : ITransferProgressNotifier
    {
        public List<TransferProgressUpdate> Updates { get; } = [];

        // Records on the calling thread so the order of updates is the order they were sent.
        public Task SendProgressAsync(TransferProgressUpdate update, CancellationToken ct)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }

        public Task NotifyListChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
