using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Services.BackgroundJobs;
using FluentAssertions;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class TransfersControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly Mock<ITransferOrchestrator> _orchestrator;
    private readonly Mock<IBackgroundJobClient> _backgroundJobs;
    private readonly Mock<ITransferProgressNotifier> _notifier = new();
    private readonly TransfersController _sut;

    public TransfersControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"TxCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);

        _orchestrator = new Mock<ITransferOrchestrator>();
        _backgroundJobs = new Mock<IBackgroundJobClient>();

        _backgroundJobs
            .Setup(b => b.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()))
            .Returns("job-123");

        _sut = new TransfersController(
            _db, _orchestrator.Object, _backgroundJobs.Object,
            _notifier.Object,
            Mock.Of<ILogger<TransfersController>>());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // =========================================================================
    // GET transfers
    // =========================================================================

    [Fact]
    public async Task GetTransfers_ReturnsPaginatedList()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);

        for (int i = 0; i < 5; i++)
        {
            var transfer = TestData.CreateCardTransfer(user.Id, $"Book {i}");
            _db.CardTransfers.Add(transfer);
        }
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetTransfers(ct: CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTransfers_WithStatusFilter_FiltersCorrectly()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);

        var t1 = TestData.CreateCardTransfer(user.Id, "Done");
        t1.Status = TransferStatus.Completed;
        var t2 = TestData.CreateCardTransfer(user.Id, "Pending");
        t2.Status = TransferStatus.Pending;
        _db.CardTransfers.AddRange(t1, t2);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetTransfers(status: TransferStatus.Completed, ct: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    // =========================================================================
    // GET transfer by ID
    // =========================================================================

    [Fact]
    public async Task GetTransfer_Found_Returns200()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        var transfer = TestData.CreateCardTransfer(user.Id, "Test Book");
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetTransfer_NotFound_Returns404()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetTransfer(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetTransfer_OwnedByAnotherUser_Returns404()
    {
        var owner = TestData.CreateUserConnection(username: "owner");
        _db.UserConnections.Add(owner);
        var transfer = TestData.CreateCardTransfer(owner.Id, "Secret Book");
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        // A different authenticated user must not see someone else's transfer.
        var result = await _sut.AsUser(Guid.NewGuid()).GetTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // =========================================================================
    // POST book transfer
    // =========================================================================

    [Fact]
    public async Task TransferBook_EnqueuesJob_Returns202()
    {
        var request = new CreateTransferRequest("item-1");
        var result = await _sut.AsUser(Guid.NewGuid()).TransferBook(request, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    // =========================================================================
    // POST series transfer
    // =========================================================================

    [Fact]
    public async Task TransferSeries_EnqueuesJob_Returns202()
    {
        var request = new CreateSeriesTransferRequest("ser-1", "lib-1");
        var result = await _sut.AsUser(Guid.NewGuid()).TransferSeries(request, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    // =========================================================================
    // POST batch transfer (Phase 2)
    // =========================================================================

    [Fact]
    public async Task TransferBatch_EnqueuesJobPerBook()
    {
        var request = new BatchTransferRequest(["item-1", "item-2", "item-3"]);
        var result = await _sut.AsUser(Guid.NewGuid()).TransferBatch(request, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value as BatchTransferResponse;
        response.Should().NotBeNull();
        response!.TotalBooks.Should().Be(3);
        response.Queued.Should().Be(3);
        response.JobIds.Should().HaveCount(3);
    }

    [Fact]
    public async Task TransferBatch_SingleItem_Works()
    {
        var request = new BatchTransferRequest(["item-1"]);
        var result = await _sut.AsUser(Guid.NewGuid()).TransferBatch(request, CancellationToken.None);

        var accepted = result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value as BatchTransferResponse;
        response!.TotalBooks.Should().Be(1);
    }

    // =========================================================================
    // POST retry + cancel
    // =========================================================================

    [Fact]
    public async Task RetryTransfer_EnqueuesJob_Returns202()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        var transfer = TestData.CreateCardTransfer(user.Id, "Test Book");
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).RetryTransfer(transfer.Id, CancellationToken.None);
        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task RetryTransfer_NotOwned_Returns404()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).RetryTransfer(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CancelTransfer_CallsOrchestrator()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        var transfer = TestData.CreateCardTransfer(user.Id, "Test Book");
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).CancelTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _orchestrator.Verify(o => o.CancelTransferAsync(transfer.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelTransfer_NotOwned_Returns404()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).CancelTransfer(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
        _orchestrator.Verify(o => o.CancelTransferAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // Mutation-testing additions — who can see, dedupe against, retry, cancel and delete what
    // =========================================================================

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private async Task<UserConnection> AddUserAsync(string username)
    {
        var user = TestData.CreateUserConnection(username: username);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<CardTransfer> AddTransferAsync(
        Guid userId, string title = "Book", string itemId = "item-1",
        TransferStatus status = TransferStatus.Pending, int minutesAgo = 0)
    {
        var transfer = TestData.CreateCardTransfer(userId, itemId, title, status);
        transfer.CreatedAt = Now.AddMinutes(-minutesAgo);
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();
        return transfer;
    }

    // ---- listing: only the caller's transfers, newest first, paged, filtered

    [Fact]
    public async Task GetTransfers_ReturnsOnlyTheCallersTransfers()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferAsync(alice.Id, "Alice 1");
        await AddTransferAsync(alice.Id, "Alice 2", minutesAgo: 1);
        await AddTransferAsync(bob.Id, "Bob 1");

        var page = ((OkObjectResult)await _sut.AsUser(alice.Id).GetTransfers(ct: CancellationToken.None)).Value.AsJson();

        page.ResultTitles().Should().BeEquivalentTo("Alice 1", "Alice 2");
        page.GetProperty("Total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetTransfers_ReturnsNewestFirst()
    {
        var user = await AddUserAsync("alice");
        await AddTransferAsync(user.Id, "Oldest", minutesAgo: 30);
        await AddTransferAsync(user.Id, "Newest", minutesAgo: 1);
        await AddTransferAsync(user.Id, "Middle", minutesAgo: 10);

        var page = ((OkObjectResult)await _sut.AsUser(user.Id).GetTransfers(ct: CancellationToken.None)).Value.AsJson();

        page.ResultTitles().Should().Equal("Newest", "Middle", "Oldest");
    }

    [Fact]
    public async Task GetTransfers_ReturnsTheRequestedPage()
    {
        var user = await AddUserAsync("alice");
        for (var i = 0; i < 5; i++)
            await AddTransferAsync(user.Id, $"T{i}", minutesAgo: i);

        var page = ((OkObjectResult)await _sut.AsUser(user.Id).GetTransfers(page: 1, limit: 2, ct: CancellationToken.None)).Value.AsJson();

        page.ResultTitles().Should().Equal("T2", "T3");
        (page.GetProperty("Total").GetInt32(), page.GetProperty("Page").GetInt32(), page.GetProperty("Limit").GetInt32())
            .Should().Be((5, 1, 2));
    }

    [Fact]
    public async Task GetTransfers_WithStatus_ReturnsAndCountsOnlyThatStatus()
    {
        var user = await AddUserAsync("alice");
        await AddTransferAsync(user.Id, "Done 1", status: TransferStatus.Completed);
        await AddTransferAsync(user.Id, "Done 2", status: TransferStatus.Completed, minutesAgo: 1);
        await AddTransferAsync(user.Id, "Waiting", status: TransferStatus.Pending, minutesAgo: 2);

        var page = ((OkObjectResult)await _sut.AsUser(user.Id)
            .GetTransfers(status: TransferStatus.Completed, ct: CancellationToken.None)).Value.AsJson();

        page.ResultTitles().Should().Equal("Done 1", "Done 2");
        page.GetProperty("Total").GetInt32().Should().Be(2);
    }

    // ---- detail

    [Fact]
    public async Task GetTransfer_ReturnsTrackDurationAndIconForEachChapter()
    {
        var user = await AddUserAsync("alice");
        var transfer = await AddTransferAsync(user.Id, "Chaptered");
        var icon = new GeneratedIcon
        {
            UserConnectionId = user.Id,
            Prompt = "p",
            ContextTitle = "c",
            YotoIconUrl = "https://icons.example/1.png",
        };
        _db.TrackMappings.Add(new TrackMapping
        {
            CardTransferId = transfer.Id,
            AbsFileIno = "ino",
            ChapterTitle = "Chapter One",
            ChapterIndex = 0,
            StartTime = 10,
            EndTime = 70,
            GeneratedIcon = icon,
        });
        await _db.SaveChangesAsync();

        var body = ((OkObjectResult)await _sut.AsUser(user.Id).GetTransfer(transfer.Id, CancellationToken.None)).Value.AsJson();

        var track = body.GetProperty("Tracks").EnumerateArray().Single();
        (track.GetProperty("ChapterTitle").GetString(), track.GetProperty("Duration").GetDouble(),
            track.GetProperty("IconUrl").GetString())
            .Should().Be(("Chapter One", 60.0, "https://icons.example/1.png"));
    }

    // ---- queueing a book: the duplicate guard

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.DownloadingAudio)]
    [InlineData(TransferStatus.UploadingToYoto)]
    [InlineData(TransferStatus.AwaitingTranscode)]
    [InlineData(TransferStatus.GeneratingIcons)]
    [InlineData(TransferStatus.CreatingCard)]
    public async Task TransferBook_WhileTheSameBookIsInFlight_Returns409AndQueuesNothing(TransferStatus inFlight)
    {
        var user = await AddUserAsync("alice");
        await AddTransferAsync(user.Id, itemId: "item-1", status: inFlight);

        var result = await _sut.AsUser(user.Id).TransferBook(new CreateTransferRequest("item-1"), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        _backgroundJobs.VerifyNothingEnqueued();
    }

    [Theory]
    [InlineData(TransferStatus.Completed)]
    [InlineData(TransferStatus.Failed)]
    [InlineData(TransferStatus.Cancelled)]
    public async Task TransferBook_WhenTheEarlierTransferHasEnded_QueuesAgain(TransferStatus ended)
    {
        var user = await AddUserAsync("alice");
        await AddTransferAsync(user.Id, itemId: "item-1", status: ended);

        var result = await _sut.AsUser(user.Id).TransferBook(new CreateTransferRequest("item-1"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task TransferBook_IgnoresAnotherUsersInFlightTransferOfTheSameBook()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferAsync(bob.Id, itemId: "item-1", status: TransferStatus.Pending);

        var result = await _sut.AsUser(alice.Id).TransferBook(new CreateTransferRequest("item-1"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task TransferBook_AllowsADifferentBookWhileAnotherIsInFlight()
    {
        var user = await AddUserAsync("alice");
        await AddTransferAsync(user.Id, itemId: "item-1", status: TransferStatus.Pending);

        var result = await _sut.AsUser(user.Id).TransferBook(new CreateTransferRequest("item-2"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task TransferBook_EnqueuesTheJobForTheCallerAndTellsClientsTheListChanged()
    {
        var user = await AddUserAsync("alice");
        var request = new CreateTransferRequest("item-1");

        var result = await _sut.AsUser(user.Id).TransferBook(request, CancellationToken.None);

        var body = ((AcceptedResult)result).Value.AsJson();
        _backgroundJobs.VerifyEnqueued<ITransferJobService>(
            "ExecuteBookTransferAsync",
            args => (Guid)args[0] == user.Id && Equals(args[1], request) && (Guid?)args[2] == body.GetProperty("TransferId").GetGuid());
        body.GetProperty("JobId").GetString().Should().Be("job-123");
        _notifier.Verify(n => n.NotifyListChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferSeries_EnqueuesTheJobForTheCallerAndTellsClientsTheListChanged()
    {
        var user = await AddUserAsync("alice");
        var request = new CreateSeriesTransferRequest("ser-1", "lib-1");

        var result = await _sut.AsUser(user.Id).TransferSeries(request, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        _backgroundJobs.VerifyEnqueued<ITransferJobService>(
            "ExecuteSeriesTransferAsync", args => (Guid)args[0] == user.Id && Equals(args[1], request));
        _notifier.Verify(n => n.NotifyListChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferBatch_QueuesOneJobPerBookForTheCaller_WithTheSharedOptions()
    {
        var user = await AddUserAsync("alice");
        var request = new BatchTransferRequest(["item-1", "item-2"], YotoCategory.Music, PlaybackType.Interactive, 4, 9);

        var result = await _sut.AsUser(user.Id).TransferBatch(request, CancellationToken.None);

        foreach (var itemId in request.AbsLibraryItemIds)
        {
            _backgroundJobs.VerifyEnqueued<ITransferJobService>(
                "ExecuteBookTransferAsync",
                args => (Guid)args[0] == user.Id
                        && Equals(args[1], new CreateTransferRequest(itemId, YotoCategory.Music, PlaybackType.Interactive, 4, 9)));
        }

        var response = (BatchTransferResponse)((AcceptedResult)result).Value!;
        response.BatchId.Should().MatchRegex("^[0-9a-f]{12}$");
        _notifier.Verify(n => n.NotifyListChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- retry and cancel: another user's transfer is "not found", and nothing happens

    [Fact]
    public async Task RetryTransfer_OwnedByAnotherUser_Returns404AndQueuesNothing()
    {
        var owner = await AddUserAsync("owner");
        var transfer = await AddTransferAsync(owner.Id);

        var result = await _sut.AsUser(Guid.NewGuid()).RetryTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _backgroundJobs.VerifyNothingEnqueued();
    }

    [Fact]
    public async Task RetryTransfer_Owned_QueuesTheRetryForThatTransfer()
    {
        var user = await AddUserAsync("alice");
        var transfer = await AddTransferAsync(user.Id);

        await _sut.AsUser(user.Id).RetryTransfer(transfer.Id, CancellationToken.None);

        _backgroundJobs.VerifyEnqueued<ITransferJobService>(
            "ExecuteRetryTransferAsync", args => (Guid)args[0] == transfer.Id);
    }

    [Fact]
    public async Task CancelTransfer_OwnedByAnotherUser_Returns404AndDoesNotCancel()
    {
        var owner = await AddUserAsync("owner");
        var transfer = await AddTransferAsync(owner.Id);

        var result = await _sut.AsUser(Guid.NewGuid()).CancelTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _orchestrator.Verify(o => o.CancelTransferAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- delete one

    [Fact]
    public async Task DeleteTransfer_OwnedByAnotherUser_Returns404AndKeepsIt()
    {
        var owner = await AddUserAsync("owner");
        var transfer = await AddTransferAsync(owner.Id, status: TransferStatus.Completed);

        var result = await _sut.AsUser(Guid.NewGuid()).DeleteTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await _db.CardTransfers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteTransfer_Unknown_Returns404()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).DeleteTransfer(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData(TransferStatus.Completed)]
    [InlineData(TransferStatus.Failed)]
    [InlineData(TransferStatus.Cancelled)]
    public async Task DeleteTransfer_WhenEnded_RemovesTheTransferAndItsTracks(TransferStatus ended)
    {
        var user = await AddUserAsync("alice");
        var transfer = await AddTransferAsync(user.Id, status: ended);
        _db.TrackMappings.Add(new TrackMapping { CardTransferId = transfer.Id, AbsFileIno = "i", ChapterTitle = "c" });
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).DeleteTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await _db.CardTransfers.CountAsync(), await _db.TrackMappings.CountAsync()).Should().Be((0, 0));
    }

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.DownloadingAudio)]
    [InlineData(TransferStatus.UploadingToYoto)]
    [InlineData(TransferStatus.AwaitingTranscode)]
    [InlineData(TransferStatus.GeneratingIcons)]
    [InlineData(TransferStatus.CreatingCard)]
    public async Task DeleteTransfer_WhileStillRunning_Returns409AndKeepsIt(TransferStatus running)
    {
        var user = await AddUserAsync("alice");
        var transfer = await AddTransferAsync(user.Id, status: running);

        var result = await _sut.AsUser(user.Id).DeleteTransfer(transfer.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        (await _db.CardTransfers.CountAsync()).Should().Be(1);
    }

    // ---- clear completed

    [Fact]
    public async Task ClearCompleted_RemovesOnlyTheCallersCompletedTransfersAndTheirTracks()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        var mine = await AddTransferAsync(alice.Id, "Mine done", status: TransferStatus.Completed);
        await AddTransferAsync(alice.Id, "Mine running", status: TransferStatus.Pending);
        await AddTransferAsync(bob.Id, "Bob done", status: TransferStatus.Completed);
        _db.TrackMappings.Add(new TrackMapping { CardTransferId = mine.Id, AbsFileIno = "i", ChapterTitle = "c" });
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(alice.Id).ClearCompleted(CancellationToken.None);

        ((OkObjectResult)result).Value.AsJson().GetProperty("Cleared").GetInt32().Should().Be(1);
        (await _db.CardTransfers.Select(t => t.BookTitle).ToArrayAsync()).Should().BeEquivalentTo("Mine running", "Bob done");
        (await _db.TrackMappings.CountAsync()).Should().Be(0);
    }
}
