using System.Diagnostics.Metrics;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Observability;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

// Mutation-testing additions — the transfer state machine: stages, notifications, metrics, failure,
// cancellation, retry and clean-up.
public partial class TransferOrchestratorTests
{
    private List<TransferProgressUpdate> CaptureNotifications()
    {
        var updates = new List<TransferProgressUpdate>();
        _notifier.Setup(n => n.SendProgressAsync(It.IsAny<TransferProgressUpdate>(), It.IsAny<CancellationToken>()))
            .Callback<TransferProgressUpdate, CancellationToken>((update, _) => updates.Add(update))
            .Returns(Task.CompletedTask);
        return updates;
    }

    /// <summary>Totals what <see cref="TransferMetrics"/> reports, by counter name, while the returned scope is alive.</summary>
    private static (Dictionary<string, long> Totals, IDisposable Scope) ListenToTransferMetrics()
    {
        var totals = new Dictionary<string, long>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == TransferMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (totals) totals[instrument.Name] = totals.GetValueOrDefault(instrument.Name) + value;
        });
        listener.Start();
        return (totals, listener);
    }

    private void ServeItemWith(AbsLibraryItem item) =>
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

    private async Task<CardTransfer> ReadTransferAsync(Guid id)
    {
        await using var fresh = _dbFixture.NewContext();
        return await fresh.CardTransfers.Include(t => t.TrackMappings).SingleAsync(t => t.Id == id);
    }

    // --- Stages, progress and notifications

    [Fact]
    public async Task TransferBookAsync_ReportsEachStageInOrderWithItsProgressAndLabel()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        var updates = CaptureNotifications();

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        updates.Select(u => (u.Status, u.ProgressPercent, u.CurrentStep)).Should().Equal(
            (TransferStatus.DownloadingAudio, 5, "Downloading audio"),
            (TransferStatus.UploadingToYoto, 20, "Uploading & transcoding on Yoto"),
            (TransferStatus.UploadingToYoto, 20, "Track 1/1 is on Yoto"),
            (TransferStatus.GeneratingIcons, 70, "Generating chapter icons"),
            (TransferStatus.CreatingCard, 85, "Creating Yoto card"),
            (TransferStatus.Completed, 100, "Transfer complete"));
    }

    [Fact]
    public async Task TransferBookAsync_WhenCompleted_SavesTheCardIdAndCompletionAndCountsIt()
    {
        var (totals, scope) = ListenToTransferMetrics();
        using var _ = scope;
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        var transferId = Guid.NewGuid();

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        var stored = await ReadTransferAsync(transferId);
        (stored.Status, stored.ProgressPercent, stored.YotoCardId, stored.ErrorMessage)
            .Should().Be((TransferStatus.Completed, 100, "card-id-123", null));
        stored.CompletedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        totals.GetValueOrDefault("ays.transfers.completed").Should().Be(1);
        totals.GetValueOrDefault("ays.transfers.failed").Should().Be(0);
    }

    [Fact]
    public async Task TransferBookAsync_UsesTheGivenTransferIdForANewRecord()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        var transferId = Guid.NewGuid();

        var response = await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        response.Id.Should().Be(transferId);
        (await ReadTransferAsync(transferId)).UserConnectionId.Should().Be(user.Id);
    }

    [Fact]
    public async Task TransferBookAsync_RecordsThePendingTransferBeforeFetchingTheItem()
    {
        var user = await SeedUserAsync();
        var transferId = Guid.NewGuid();
        (TransferStatus Status, string Title, string Reason)? seenDuringFetch = null;
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var fresh = _dbFixture.NewContext();
                var row = await fresh.CardTransfers.SingleOrDefaultAsync(t => t.Id == transferId);
                seenDuringFetch = row is null ? null : (row.Status, row.BookTitle, row.AgeSuggestionReason);
                return TestData.CreateAbsLibraryItem();
            });

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        seenDuringFetch.Should().Be((TransferStatus.Pending, "(pending)", "(pending)"));
    }

    [Fact]
    public async Task TransferBookAsync_StoresTheItemMetadataAndTheAgeSuggestion()
    {
        var user = await SeedUserAsync();
        var metadata = TestData.CreateAbsMetadata("Saga", seriesName: "The Saga", seriesSequence: "2.5");
        ServeItemWith(TestData.CreateAbsLibraryItem(media: TestData.CreateAbsMedia(metadata)));
        _ageService.Setup(s => s.SuggestAgeRange(It.IsAny<AbsBookMetadata>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(new AgeSuggestionResponse(7, 11, "because", AgeRangeSource.KeywordInferred, []));
        var transferId = Guid.NewGuid();

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        var stored = await ReadTransferAsync(transferId);
        (stored.BookTitle, stored.BookAuthor, stored.SeriesName, stored.SeriesSequence)
            .Should().Be(("Saga", "Test Author", "The Saga", 2.5f));
        (stored.SuggestedMinAge, stored.SuggestedMaxAge, stored.AgeSuggestionReason, stored.AgeSuggestionSource)
            .Should().Be((7, 11, "because", AgeRangeSource.KeywordInferred));
    }

    [Fact]
    public async Task TransferBookAsync_ItemWithoutTitleAuthorOrSeries_FallsBackToUnknownAndNulls()
    {
        var user = await SeedUserAsync();
        var metadata = TestData.CreateAbsMetadata() with { Title = null, Authors = [], Series = [] };
        ServeItemWith(TestData.CreateAbsLibraryItem(media: TestData.CreateAbsMedia(metadata)));
        var transferId = Guid.NewGuid();

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        var stored = await ReadTransferAsync(transferId);
        (stored.BookTitle, stored.BookAuthor, stored.SeriesName, stored.SeriesSequence)
            .Should().Be(("Unknown", null, null, null));
    }

    [Fact]
    public async Task TransferBookAsync_RefreshesAnExpiringAudiobookshelfTokenBeforeFetchingTheItem()
    {
        var user = TestData.CreateUserConnection(
            absToken: "stale", absRefreshToken: "refresh-old", absTokenExpiry: DateTimeOffset.UtcNow.AddMinutes(1));
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.RefreshTokenAsync(user.AudiobookshelfUrl, "refresh-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLoginResponse(
                new AbsUser("u1", "testuser", "user", "legacy", true, null, null, "renewed", "refresh-new"), null));
        ServeItemWith(TestData.CreateAbsLibraryItem());

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _absService.Verify(s => s.GetLibraryItemAsync(
            user.AudiobookshelfUrl, "renewed", "item-123", It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Cover art

    [Fact]
    public async Task TransferBookAsync_UploadsTheItemCoverAndPutsItOnTheCard()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        YotoCardMetadata? sent = null;
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, YotoCardContent, YotoCardMetadata, string?, string?, CancellationToken>((_, _, m, _, _, _) => sent = m)
            .ReturnsAsync("card-id-123");

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _absService.Verify(s => s.GetCoverImageAsync(
            user.AudiobookshelfUrl, user.AudiobookshelfToken!, "item-123", It.IsAny<CancellationToken>()), Times.Once);
        sent!.Cover.Should().Be(new YotoCover("https://covers.yotoplay.com/test.jpg"));
    }

    [Fact]
    public async Task TransferBookAsync_WhenTheCoverUploadFails_StillCompletesWithoutACover()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        _yotoService.Setup(s => s.UploadCoverImageAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("cover rejected"));
        YotoCardMetadata? sent = null;
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, YotoCardContent, YotoCardMetadata, string?, string?, CancellationToken>((_, _, m, _, _, _) => sent = m)
            .ReturnsAsync("card-id-123");

        var response = await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        response.Status.Should().Be(TransferStatus.Completed);
        sent!.Cover.Should().BeNull();
    }

    // --- Failure

    [Fact]
    public async Task TransferBookAsync_WhenAStageThrows_SavesTheFailureNotifiesAndCountsIt()
    {
        var (totals, scope) = ListenToTransferMetrics();
        using var _ = scope;
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var updates = CaptureNotifications();
        var transferId = Guid.NewGuid();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        var stored = await ReadTransferAsync(transferId);
        (stored.Status, stored.ErrorMessage, stored.BookTitle, stored.AgeSuggestionReason)
            .Should().Be((TransferStatus.Failed, "boom", "(pending)", "(pending)"));
        updates.Single().Should().Be(new TransferProgressUpdate(transferId, TransferStatus.Failed, 0, "Transfer failed", "boom"));
        totals.GetValueOrDefault("ays.transfers.failed").Should().Be(1);
        totals.GetValueOrDefault("ays.transfers.completed").Should().Be(0);
    }

    [Fact]
    public async Task TransferBookAsync_ItemWithoutMedia_FailsWithThatReason()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem() with { Media = null });
        var transferId = Guid.NewGuid();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Item has no media");
        (await ReadTransferAsync(transferId)).ErrorMessage.Should().Be("Item has no media");
    }

    [Fact]
    public async Task TransferBookAsync_LongErrorMessage_IsStoredTruncatedTo4000Characters()
    {
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(new string('x', 4001)));
        var transferId = Guid.NewGuid();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReadTransferAsync(transferId)).ErrorMessage.Should().Be(new string('x', 4000));
    }

    // --- Cancellation

    [Fact]
    public async Task TransferBookAsync_CancelledWhileRunning_StopsSavesCancelledAndIsNotCountedAsFailed()
    {
        var (totals, scope) = ListenToTransferMetrics();
        using var _ = scope;
        var user = await SeedUserAsync();
        var transferId = Guid.NewGuid();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // The user cancels from another request while the job is fetching the item.
                await using var other = _dbFixture.NewContext();
                (await other.CardTransfers.SingleAsync(t => t.Id == transferId)).Status = TransferStatus.Cancelled;
                await other.SaveChangesAsync();
                return TestData.CreateAbsLibraryItem();
            });
        var updates = CaptureNotifications();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        await act.Should().ThrowAsync<OperationCanceledException>().WithMessage("Transfer was cancelled");
        (await ReadTransferAsync(transferId)).Status.Should().Be(TransferStatus.Cancelled);
        updates.Single().Should().Be(new TransferProgressUpdate(transferId, TransferStatus.Cancelled, 0, "Transfer cancelled", null));
        VerifyTrackUploads(Times.Never());
        totals.GetValueOrDefault("ays.transfers.failed").Should().Be(0);
    }

    [Fact]
    public async Task TransferBookAsync_CancelledByTheCallersToken_SavesTheCancellation()
    {
        var user = await SeedUserAsync();
        var transferId = Guid.NewGuid();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadTransferAsync(transferId)).Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task TransferBookAsync_SavesEachStageAsItStartsSoThePollingEndpointSeesIt()
    {
        var user = await SeedUserAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());
        var transferId = Guid.NewGuid();
        var seen = new List<(string During, TransferStatus Status, int Progress)>();
        async Task Snapshot(string during)
        {
            var stored = await ReadTransferAsync(transferId);
            seen.Add((during, stored.Status, stored.ProgressPercent));
        }
        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Snapshot("download");
                return new MemoryStream(new byte[100]);
            });
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Snapshot("upload");
                return "sha";
            });
        _iconService.Setup(s => s.GenerateChapterIconAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Snapshot("icons");
                return new byte[] { 1 };
            });
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Snapshot("card");
                return "card-1";
            });

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        seen.Should().Equal(
            ("download", TransferStatus.DownloadingAudio, 5),
            ("upload", TransferStatus.UploadingToYoto, 20),
            ("icons", TransferStatus.GeneratingIcons, 70),
            ("card", TransferStatus.CreatingCard, 85));
    }

    [Fact]
    public async Task TransferBookAsync_ExistingCancelledTransfer_IsReturnedWithoutRunning()
    {
        var user = await SeedUserAsync();
        var cancelled = TestData.CreateCardTransfer(user.Id, status: TransferStatus.Cancelled);
        _db.CardTransfers.Add(cancelled);
        await _db.SaveChangesAsync();

        var response = await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), cancelled.Id);

        response.Status.Should().Be(TransferStatus.Cancelled);
        _absService.Verify(s => s.GetLibraryItemAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Retry

    [Fact]
    public async Task TransferBookAsync_RetryWithAnExistingTransfer_ReplacesOnlyThatTransfersTrackMappings()
    {
        var user = await SeedUserAsync();
        var failed = TestData.CreateCardTransfer(user.Id, status: TransferStatus.Failed);
        var other = TestData.CreateCardTransfer(user.Id, "other-item");
        _db.CardTransfers.AddRange(failed, other);
        _db.TrackMappings.AddRange(
            TestData.CreateTrackMapping(failed.Id, "stale-ino"),
            TestData.CreateTrackMapping(other.Id, "other-ino"));
        await _db.SaveChangesAsync();
        ServeItemWith(TestData.CreateAbsLibraryItem());

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), failed.Id);

        var mine = await ReadTransferAsync(failed.Id);
        mine.TrackMappings.Select(m => m.AbsFileIno).Should().Equal("ino-1:ch0");
        (await ReadTransferAsync(other.Id)).TrackMappings.Select(m => m.AbsFileIno).Should().Equal("other-ino");
    }

    [Fact]
    public async Task RetryTransferAsync_ResetsTheTransferAndItsMappingsEvenIfTheRerunFailsImmediately()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null);   // rerun fails at once: no Yoto connection
        _db.UserConnections.Add(user);
        var failed = TestData.CreateCardTransfer(user.Id, status: TransferStatus.Failed);
        failed.ErrorMessage = "old failure";
        failed.ProgressPercent = 40;
        failed.CompletedAt = DateTimeOffset.UtcNow;
        var other = TestData.CreateCardTransfer(user.Id, "other-item");
        _db.CardTransfers.AddRange(failed, other);
        _db.TrackMappings.AddRange(
            TestData.CreateTrackMapping(failed.Id, "stale-ino"),
            TestData.CreateTrackMapping(other.Id, "other-ino"));
        await _db.SaveChangesAsync();

        var act = () => _sut.RetryTransferAsync(failed.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Yoto*");
        var stored = await ReadTransferAsync(failed.Id);
        (stored.Status, stored.ErrorMessage, stored.ProgressPercent, stored.CompletedAt)
            .Should().Be((TransferStatus.Pending, null, 0, null));
        stored.TrackMappings.Should().BeEmpty();
        (await ReadTransferAsync(other.Id)).TrackMappings.Should().ContainSingle();
    }

    [Fact]
    public async Task RetryTransferAsync_UnknownTransfer_ThrowsNotFound()
    {
        var act = () => _sut.RetryTransferAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Transfer not found");
    }

    [Fact]
    public async Task CancelTransferAsync_SavesTheCancellation()
    {
        var user = await SeedUserAsync();
        var running = TestData.CreateCardTransfer(user.Id, status: TransferStatus.UploadingToYoto);
        _db.CardTransfers.Add(running);
        await _db.SaveChangesAsync();

        await _sut.CancelTransferAsync(running.Id);

        (await ReadTransferAsync(running.Id)).Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task CancelTransferAsync_UnknownTransfer_ThrowsNotFound()
    {
        var act = () => _sut.CancelTransferAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Transfer not found");
    }

    [Fact]
    public async Task GetTransferStatusAsync_UnknownTransfer_ThrowsNotFound()
    {
        var act = () => _sut.GetTransferStatusAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Transfer not found");
    }

    [Fact]
    public async Task GetTransferStatusAsync_ReportsEachTracksDurationAndUploadState()
    {
        var user = await SeedUserAsync();
        var transfer = TestData.CreateCardTransfer(user.Id);
        var uploaded = TestData.CreateTrackMapping(transfer.Id, "ino-a", "Uploaded", 0, "sha-a");
        uploaded.StartTime = 10;
        uploaded.EndTime = 70;
        var pending = TestData.CreateTrackMapping(transfer.Id, "ino-b", "Pending", 1);
        pending.StartTime = 0;
        pending.EndTime = 30;
        _db.CardTransfers.Add(transfer);
        _db.TrackMappings.AddRange(uploaded, pending);
        await _db.SaveChangesAsync();

        var response = await _sut.GetTransferStatusAsync(transfer.Id);

        response.Tracks.Select(t => (t.ChapterTitle, t.ChapterIndex, t.Duration, t.IsUploaded))
            .Should().BeEquivalentTo(new[] { ("Uploaded", 0, 60.0, true), ("Pending", 1, 30.0, false) });
    }

    // --- Series

    [Fact]
    public async Task TransferSeriesAsync_TransfersBooksBySequenceWithUnnumberedBooksLast()
    {
        var user = await SeedUserAsync();
        AbsSeriesBook Book(string id, string? sequence) => new(id, TestData.CreateAbsMedia(), sequence);
        var series = new AbsSeriesItem("series-1", "Saga", null,
            [Book("b3", "3"), Book("b-none", null), Book("b1", "1"), Book("b2", "2")], 0);
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);
        var fetched = new List<string>();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string id, CancellationToken _) =>
            {
                fetched.Add(id);
                return TestData.CreateAbsLibraryItem(id);
            });

        await _sut.TransferSeriesAsync(user.Id, TestData.CreateSeriesTransferRequest());

        fetched.Should().Equal("b1", "b2", "b3", "b-none");
    }

    [Fact]
    public async Task TransferSeriesAsync_PassesTheCategoryAndAgeOverridesToEveryBook()
    {
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsSeriesItem("Saga", 2));
        ServeItemWith(TestData.CreateAbsLibraryItem());

        await _sut.TransferSeriesAsync(user.Id,
            new CreateSeriesTransferRequest("series-1", "lib-1", YotoCategory.Music, OverrideMinAge: 6, OverrideMaxAge: 9));

        var stored = await _db.CardTransfers.ToListAsync();
        stored.Should().HaveCount(2).And.OnlyContain(t =>
            t.Category == YotoCategory.Music && t.OverrideMinAge == 6 && t.OverrideMaxAge == 9);
    }

    // --- Clean-up of temp files

    [Fact]
    public async Task TransferBookAsync_RemovesItsOwnTempFilesButNotAnotherTransfersAfterwards()
    {
        var tempDir = Directory.CreateTempSubdirectory("ays-cleanup-").FullName;
        try
        {
            var sut = CreateSut(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = tempDir })
                .Build());
            var user = await SeedUserAsync();
            ServeItemWith(TestData.CreateAbsLibraryItem());
            var transferId = Guid.NewGuid();
            var mine = Path.Combine(tempDir, $"{transferId}_leftover.tmp");
            var theirs = Path.Combine(tempDir, $"{Guid.NewGuid()}_leftover.tmp");
            File.WriteAllText(mine, "x");
            File.WriteAllText(theirs, "x");

            await sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

            File.Exists(mine).Should().BeFalse();
            File.Exists(theirs).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
