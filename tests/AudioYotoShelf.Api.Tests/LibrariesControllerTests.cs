using System.Text.Json;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class LibrariesControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly Mock<IAudiobookshelfService> _absService;
    private readonly Mock<IAgeSuggestionService> _ageService;
    private readonly LibrariesController _sut;

    public LibrariesControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"LibCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);

        _absService = new Mock<IAudiobookshelfService>();
        _ageService = new Mock<IAgeSuggestionService>();

        _sut = new LibrariesController(
            _absService.Object, _ageService.Object, _db,
            Mock.Of<ILogger<LibrariesController>>());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // =========================================================================
    // GetLibraryItems — search passthrough
    // =========================================================================

    [Fact]
    public async Task GetItems_WithSort_PassesSortToService()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLibraryItemsResponse([], 0, 20, 0));

        await _sut.AsUser(user.Id).GetLibraryItems("lib-1", sort: "media.duration");

        _absService.Verify(s => s.GetLibraryItemsAsync(
            It.IsAny<string>(), It.IsAny<string>(), "lib-1",
            0, 20, "media.duration", false, false,
            null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetItems_NoSort_DefaultsToTitle()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLibraryItemsResponse([], 0, 20, 0));

        await _sut.AsUser(user.Id).GetLibraryItems("lib-1");

        _absService.Verify(s => s.GetLibraryItemsAsync(
            It.IsAny<string>(), It.IsAny<string>(), "lib-1",
            0, 20, "media.metadata.title", false, false,
            null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetItems_WithPagination_PassesPageAndLimit()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLibraryItemsResponse([], 100, 10, 3));

        var result = await _sut.AsUser(user.Id).GetLibraryItems("lib-1", page: 3, limit: 10);

        result.Should().BeOfType<OkObjectResult>();
        _absService.Verify(s => s.GetLibraryItemsAsync(
            It.IsAny<string>(), It.IsAny<string>(), "lib-1",
            3, 10, It.IsAny<string?>(), false, false,
            null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // Auth checks
    // =========================================================================

    [Fact]
    public async Task GetItems_NoConnection_Returns401()
    {
        // Authenticated, but the connection row doesn't exist.
        var result = await _sut.AsUser(Guid.NewGuid()).GetLibraryItems("lib-1");
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetItems_ExpiredAbsToken_Returns401()
    {
        var user = TestData.CreateUserConnection();
        user.AudiobookshelfToken = null; // Invalidate
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetLibraryItems("lib-1");
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // =========================================================================
    // GetLibraries
    // =========================================================================

    [Fact]
    public async Task GetLibraries_FiltersToBookType()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibrariesAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AbsLibrary("lib-1", "My Books", "book", null),
                new AbsLibrary("lib-2", "Podcasts", "podcast", null),
                new AbsLibrary("lib-3", "Kids Books", "book", null),
            ]);

        var result = await _sut.AsUser(user.Id).GetLibraries(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var libraries = ok.Value as AbsLibrary[];
        libraries.Should().HaveCount(2);
        libraries.Should().OnlyContain(l => l.MediaType == "book");
    }

    // =========================================================================
    // GetItem — includes age suggestion
    // =========================================================================

    [Fact]
    public async Task GetItem_ReturnsAgeSuggestion()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), "item-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem("item-1"));

        _ageService.Setup(s => s.SuggestAgeRange(
                It.IsAny<AbsBookMetadata>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(new Core.DTOs.Transfer.AgeSuggestionResponse(
                5, 10, "Genre-based", Core.Enums.AgeRangeSource.GenreInferred, []));

        var result = await _sut.AsUser(user.Id).GetItem("item-1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    // =========================================================================
    // Search routes to the dedicated ABS search endpoint
    // =========================================================================

    [Fact]
    public async Task GetItems_WithSearch_UsesSearchEndpointNotItems()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.SearchLibraryItemsAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-1", "narnia", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([TestData.CreateAbsLibraryItem("book-1")]);

        var result = await _sut.AsUser(user.Id).GetLibraryItems("lib-1", search: "narnia");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        (ok.Value as AbsLibraryItemsResponse)!.Total.Should().Be(1);

        _absService.Verify(s => s.SearchLibraryItemsAsync(
            It.IsAny<string>(), It.IsAny<string>(), "lib-1", "narnia", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _absService.Verify(s => s.GetLibraryItemsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // Multi-library series resolution
    // =========================================================================

    [Fact]
    public async Task GetSeriesDetail_WithLibraryId_UsesItDirectly()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-2", "s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsSeriesItem("Harry Potter", 3));

        var result = await _sut.AsUser(user.Id).GetSeriesDetail("s1", "lib-2", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _absService.Verify(s => s.GetLibrariesAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSeriesDetail_NoLibraryId_SearchesBookLibrariesUntilBooksFound()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibrariesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AbsLibrary("lib-a", "A", "book", null), new AbsLibrary("lib-b", "B", "book", null)]);

        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-a", "s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsSeriesItem("s1", "Harry Potter", null, [], 0)); // not here
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-b", "s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsSeriesItem("Harry Potter", 3));      // lives here

        var result = await _sut.AsUser(user.Id).GetSeriesDetail("s1", null, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        (ok.Value as AbsSeriesItem)!.Books.Should().HaveCount(3);
    }

    // =========================================================================
    // Mutation-testing additions — token refresh, existing-transfer lookup, series resolution
    // =========================================================================

    private async Task<UserConnection> AddUserAsync(string username)
    {
        var user = TestData.CreateUserConnection(username: username, absToken: $"{username}-abs-token");
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task AddTransferAsync(Guid userId, string itemId, string? cardId, int minutesAgo, TransferStatus status = TransferStatus.Completed)
    {
        var transfer = TestData.CreateCardTransfer(userId, itemId, $"{itemId} by {userId:N}", status);
        transfer.YotoCardId = cardId;
        transfer.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();
    }

    private static AbsLoginResponse LoginWith(string accessToken) => new(
        new AbsUser("u1", "user", "user", "legacy", true, null, null, accessToken, "rotated-refresh"), null);

    // ---- the stored Audiobookshelf token is refreshed before use

    [Fact]
    public async Task GetLibraries_WithAnExpiredAccessTokenAndARefreshToken_UsesTheRefreshedToken()
    {
        var user = await AddUserAsync("alice");
        user.AudiobookshelfTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        user.AudiobookshelfRefreshToken = "stored-refresh";
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.RefreshTokenAsync(user.AudiobookshelfUrl, "stored-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(LoginWith("fresh-access"));
        _absService.Setup(s => s.GetLibrariesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _sut.AsUser(user.Id).GetLibraries(CancellationToken.None);

        _absService.Verify(s => s.GetLibrariesAsync(user.AudiobookshelfUrl, "fresh-access", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLibraries_WhenTheRefreshTokenIsRejected_Returns401AndDoesNotCallAudiobookshelf()
    {
        var user = await AddUserAsync("alice");
        user.AudiobookshelfTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        user.AudiobookshelfRefreshToken = "revoked";
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("401"));

        var result = await _sut.AsUser(user.Id).GetLibraries(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _absService.Verify(s => s.GetLibrariesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetLibraries_UsesTheCallersOwnAudiobookshelfToken()
    {
        await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        _absService.Setup(s => s.GetLibrariesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _sut.AsUser(bob.Id).GetLibraries(CancellationToken.None);

        _absService.Verify(s => s.GetLibrariesAsync(bob.AudiobookshelfUrl, "bob-abs-token", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- a book's age suggestion

    [Fact]
    public async Task GetItem_SuggestsAnAgeFromTheBooksMetadataDurationAndChapters()
    {
        var user = await AddUserAsync("alice");
        var item = TestData.CreateAbsLibraryItem("item-1");
        var suggestion = new AgeSuggestionResponse(5, 10, "Genre-based", AgeRangeSource.GenreInferred, []);
        _absService.Setup(s => s.GetLibraryItemAsync(It.IsAny<string>(), It.IsAny<string>(), "item-1", It.IsAny<CancellationToken>())).ReturnsAsync(item);
        _ageService.Setup(s => s.SuggestAgeRange(item.Media!.Metadata, item.Media.Duration, item.Media.NumChapters)).Returns(suggestion);

        var body = ((OkObjectResult)await _sut.AsUser(user.Id).GetItem("item-1", CancellationToken.None)).Value.AsJson();

        body.GetProperty("AgeSuggestion").GetProperty("Reason").GetString().Should().Be("Genre-based");
        _ageService.Verify(s => s.SuggestAgeRange(item.Media!.Metadata, 3600, 10), Times.Once);
    }

    [Fact]
    public async Task GetItem_WithoutMedia_HasNoAgeSuggestionAndDoesNotAskForOne()
    {
        var user = await AddUserAsync("alice");
        var item = TestData.CreateAbsLibraryItem("item-1") with { Media = null };
        _absService.Setup(s => s.GetLibraryItemAsync(It.IsAny<string>(), It.IsAny<string>(), "item-1", It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var body = ((OkObjectResult)await _sut.AsUser(user.Id).GetItem("item-1", CancellationToken.None)).Value.AsJson();

        body.GetProperty("AgeSuggestion").ValueKind.Should().Be(JsonValueKind.Null);
        _ageService.Invocations.Should().BeEmpty();
    }

    // ---- "already transferred?": only the caller's own newest transfer of this book

    [Fact]
    public async Task GetItem_ReportsTheCallersNewestTransferOfThatBook()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferAsync(alice.Id, "item-1", "card-old", minutesAgo: 60, status: TransferStatus.Failed);
        await AddTransferAsync(alice.Id, "item-1", "card-new", minutesAgo: 30);
        await AddTransferAsync(alice.Id, "item-2", "card-other-book", minutesAgo: 1);
        await AddTransferAsync(bob.Id, "item-1", "card-bob", minutesAgo: 5);
        _absService.Setup(s => s.GetLibraryItemAsync(It.IsAny<string>(), It.IsAny<string>(), "item-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem("item-1"));

        var body = ((OkObjectResult)await _sut.AsUser(alice.Id).GetItem("item-1", CancellationToken.None)).Value.AsJson();

        body.GetProperty("ExistingTransfer").GetProperty("YotoCardId").GetString().Should().Be("card-new");
    }

    [Fact]
    public async Task GetItem_WhenOnlyAnotherUserTransferredTheBook_ReportsNoExistingTransfer()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferAsync(bob.Id, "item-1", "card-bob", minutesAgo: 5);
        _absService.Setup(s => s.GetLibraryItemAsync(It.IsAny<string>(), It.IsAny<string>(), "item-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem("item-1"));

        var body = ((OkObjectResult)await _sut.AsUser(alice.Id).GetItem("item-1", CancellationToken.None)).Value.AsJson();

        body.GetProperty("ExistingTransfer").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- series list, cover: guard and pass-through

    [Fact]
    public async Task GetSeries_WithoutAConnection_Returns401()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetSeries("lib-1", 0, 20, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _absService.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSeries_PassesLibraryAndPagingToAudiobookshelf()
    {
        var user = await AddUserAsync("alice");
        var series = new AbsSeriesResponse([], 0, 5, 2);
        _absService.Setup(s => s.GetSeriesAsync(user.AudiobookshelfUrl, "alice-abs-token", "lib-1", 2, 5, It.IsAny<CancellationToken>())).ReturnsAsync(series);

        var result = await _sut.AsUser(user.Id).GetSeries("lib-1", 2, 5, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(series);
    }

    [Fact]
    public async Task GetCover_WithoutAConnection_Returns401()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetCover("item-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _absService.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCover_StreamsTheImageAsJpeg()
    {
        var user = await AddUserAsync("alice");
        var image = new MemoryStream([1, 2, 3]);
        _absService.Setup(s => s.GetCoverImageAsync(user.AudiobookshelfUrl, "alice-abs-token", "item-1", It.IsAny<CancellationToken>())).ReturnsAsync(image);

        var result = await _sut.AsUser(user.Id).GetCover("item-1", CancellationToken.None);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        (file.ContentType, file.FileStream).Should().Be(("image/jpeg", (Stream)image));
    }

    // ---- a series without a library: search the user's book libraries

    private void SetupBookLibraries(params string[] libraryIds) =>
        _absService.Setup(s => s.GetLibrariesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. libraryIds.Select(id => new AbsLibrary(id, id, id.StartsWith("pod") ? "podcast" : "book", null))]);

    private void SetupSeriesIn(string libraryId, AbsSeriesItem series) =>
        _absService.Setup(s => s.GetSeriesDetailAsync(It.IsAny<string>(), It.IsAny<string>(), libraryId, "s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

    [Fact]
    public async Task GetSeriesDetail_WithoutAConnection_Returns401()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetSeriesDetail("s1", null, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetSeriesDetail_WhenNoLibraryHoldsBooks_ReturnsTheFirstLibrarysSeriesInfo()
    {
        var user = await AddUserAsync("alice");
        SetupBookLibraries("lib-a", "lib-b");
        SetupSeriesIn("lib-a", new AbsSeriesItem("s1", "From A", null, [], 0));
        SetupSeriesIn("lib-b", new AbsSeriesItem("s1", "From B", null, [], 0));

        var result = await _sut.AsUser(user.Id).GetSeriesDetail("s1", null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AbsSeriesItem>().Which.Name.Should().Be("From A");
    }

    [Fact]
    public async Task GetSeriesDetail_StopsAtTheFirstLibraryThatHoldsTheSeriesBooks()
    {
        var user = await AddUserAsync("alice");
        SetupBookLibraries("lib-a", "lib-b");
        SetupSeriesIn("lib-a", TestData.CreateAbsSeriesItem("From A", 2));
        SetupSeriesIn("lib-b", TestData.CreateAbsSeriesItem("From B", 4));

        var result = await _sut.AsUser(user.Id).GetSeriesDetail("s1", null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AbsSeriesItem>().Which.Name.Should().Be("From A");
        _absService.Verify(s => s.GetSeriesDetailAsync(It.IsAny<string>(), It.IsAny<string>(), "lib-b", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSeriesDetail_WithOnlyPodcastLibraries_Returns404()
    {
        var user = await AddUserAsync("alice");
        SetupBookLibraries("pod-1");

        var result = await _sut.AsUser(user.Id).GetSeriesDetail("s1", null, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _absService.Verify(s => s.GetSeriesDetailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
