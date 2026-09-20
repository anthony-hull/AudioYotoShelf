using System.Net;
using System.Text.Json;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Api.Tests;

public class CardsControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly Mock<IYotoService> _yotoService;
    private readonly CardsController _sut;

    public CardsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"CardsCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);

        _yotoService = new Mock<IYotoService>();

        _sut = new CardsController(
            _yotoService.Object, _db,
            Mock.Of<ILogger<CardsController>>());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task GetCards_ReturnsCardsFromYotoService()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new YotoCard("card-1", null, new YotoCardMetadata("Author", "stories", "My Book", null, null, 3, 8, null, null)),
                new YotoCard("card-2", null, null)
            ]);

        var result = await _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCards_EnrichesWithTransferHistory()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);

        var transfer = TestData.CreateCardTransfer(user.Id, "Test Book");
        transfer.YotoCardId = "card-1";
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new YotoCard("card-1", null, null),
                new YotoCard("card-2", null, null)
            ]);

        var result = await _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetCards_NoYotoConnection_Returns401()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null, yotoRefreshToken: null);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetCards_UserNotFound_Returns401()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetCards(CancellationToken.None);
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task GetCard_ReturnsCardDetail()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var card = new YotoCard("card-1",
            new YotoCardContent([
                new YotoChapter("01", "Chapter 1", [
                    new YotoTrack("t1", "Track 1", "yoto:#abc", "mp3", "audio", 120, 1024, "stereo", null)
                ], null)
            ], null, "linear", "1"),
            null);

        _yotoService.Setup(s => s.GetCardContentAsync(It.IsAny<string>(), "card-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(card);

        var result = await _sut.AsUser(user.Id).GetCard("card-1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task DeleteCard_CallsYotoService()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        _yotoService.Setup(s => s.DeleteCardAsync(It.IsAny<string>(), "card-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.AsUser(user.Id).DeleteCard("card-1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _yotoService.Verify(s => s.DeleteCardAsync(user.YotoAccessToken!, "card-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteCard_NoYotoConnection_Returns401()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null, yotoRefreshToken: null);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).DeleteCard("card-1", CancellationToken.None);
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // =========================================================================
    // Mutation-testing additions — whose token and history are used, and when the token is refreshed
    // =========================================================================

    private static readonly YotoTokenResponse Refreshed = new("refreshed-token", "refreshed-refresh", "Bearer", 3600);

    private async Task<UserConnection> AddUserAsync(string username, string accessToken = "access-token")
    {
        var user = TestData.CreateUserConnection(username: username, yotoAccessToken: accessToken, yotoRefreshToken: "refresh-token");
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task AddTransferWithCardAsync(Guid userId, string? cardId, string title, string? author = null)
    {
        var transfer = TestData.CreateCardTransfer(userId, $"item-{Guid.NewGuid():N}", title);
        transfer.YotoCardId = cardId;
        transfer.BookAuthor = author;
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();
    }

    private static YotoCard CardWithChapters(string cardId, params int[] tracksPerChapter) => new(
        cardId,
        new YotoCardContent(
            [.. tracksPerChapter.Select((count, i) => new YotoChapter(
                $"{i:00}", $"Chapter {i}",
                [.. Enumerable.Range(0, count).Select(t => new YotoTrack($"t{t}", $"Track {t}", "yoto:#x", "mp3", "audio", 60, 1024, "stereo", null))],
                null))],
            null, "linear", "1"),
        null);

    private static JsonElement[] Cards(IActionResult result) =>
        [.. ((OkObjectResult)result).Value.AsJson().EnumerateArray()];

    private static JsonElement CardNamed(IEnumerable<JsonElement> cards, string cardId) =>
        cards.Single(c => c.GetProperty("CardId").GetString() == cardId);

    [Fact]
    public async Task GetCards_UsesTheCallersOwnYotoToken()
    {
        await AddUserAsync("alice", "alice-token");
        var bob = await AddUserAsync("bob", "bob-token");
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await _sut.AsUser(bob.Id).GetCards(CancellationToken.None);

        _yotoService.Verify(s => s.GetUserCardsAsync("bob-token", It.IsAny<CancellationToken>()), Times.Once);
        _yotoService.Verify(s => s.GetUserCardsAsync("alice-token", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCards_MarksAsOursOnlyCardsTheCallerTransferred()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferWithCardAsync(alice.Id, "card-1", "Alice Book", "Alice Author");
        await AddTransferWithCardAsync(alice.Id, null, "Alice Unfinished");      // no card yet: must be ignored
        await AddTransferWithCardAsync(bob.Id, "card-2", "Bob Book", "Bob Author");
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new YotoCard("card-1", null, null), new YotoCard("card-2", null, null)]);

        var cards = Cards(await _sut.AsUser(alice.Id).GetCards(CancellationToken.None));

        var mine = CardNamed(cards, "card-1");
        (mine.GetProperty("FromAudioYotoShelf").GetBoolean(), mine.GetProperty("SourceBookTitle").GetString(),
            mine.GetProperty("SourceBookAuthor").GetString())
            .Should().Be((true, "Alice Book", "Alice Author"));
        var notMine = CardNamed(cards, "card-2");
        (notMine.GetProperty("FromAudioYotoShelf").GetBoolean(), notMine.GetProperty("SourceBookTitle").ValueKind)
            .Should().Be((false, JsonValueKind.Null));
    }

    [Fact]
    public async Task GetCards_CountsChaptersAndAddsUpTheTracksInThem()
    {
        var user = await AddUserAsync("alice");
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CardWithChapters("full", 2, 3), new YotoCard("empty", null, null)]);

        var cards = Cards(await _sut.AsUser(user.Id).GetCards(CancellationToken.None));

        var full = CardNamed(cards, "full");
        (full.GetProperty("ChapterCount").GetInt32(), full.GetProperty("TrackCount").GetInt32()).Should().Be((2, 5));
        var empty = CardNamed(cards, "empty");
        (empty.GetProperty("ChapterCount").GetInt32(), empty.GetProperty("TrackCount").GetInt32()).Should().Be((0, 0));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetCards_WhenTheFamilyLibraryIsRefused_FallsBackToTheCallersOwnTransferHistory(HttpStatusCode refused)
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await AddTransferWithCardAsync(alice.Id, "card-1", "First", "Author One");
        await AddTransferWithCardAsync(alice.Id, "card-1", "First again");            // same card twice: one entry
        await AddTransferWithCardAsync(alice.Id, "card-gone", "Deleted on Yoto");     // fetch fails: skipped
        await AddTransferWithCardAsync(bob.Id, "card-bob", "Bob Book");
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("refused", null, refused));
        _yotoService.Setup(s => s.GetCardContentAsync(It.IsAny<string>(), "card-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CardWithChapters("card-1", 2, 3));
        _yotoService.Setup(s => s.GetCardContentAsync(It.IsAny<string>(), "card-gone", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("not found", null, HttpStatusCode.NotFound));

        var cards = Cards(await _sut.AsUser(alice.Id).GetCards(CancellationToken.None));

        var only = cards.Should().ContainSingle().Subject;
        (only.GetProperty("CardId").GetString(), only.GetProperty("ChapterCount").GetInt32(), only.GetProperty("TrackCount").GetInt32(),
            only.GetProperty("FromAudioYotoShelf").GetBoolean(), only.GetProperty("SourceBookAuthor").GetString())
            .Should().Be(("card-1", 2, 5, true, "Author One"));
        _yotoService.Verify(s => s.GetCardContentAsync(It.IsAny<string>(), "card-bob", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCards_WhenYotoFailsForAnotherReason_DoesNotHideTheError()
    {
        var user = await AddUserAsync("alice");
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom", null, HttpStatusCode.InternalServerError));

        var act = () => _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ---- when the access token is refreshed

    [Theory]
    [InlineData(null, true)]        // expiry unknown
    [InlineData(-60, true)]         // already expired
    [InlineData(2, true)]           // inside the five-minute margin
    [InlineData(10, false)]         // outside it
    [InlineData(600, false)]
    public async Task GetCards_RefreshesTheYotoTokenOnlyWhenItIsUnknownExpiredOrAboutToExpire(int? minutesUntilExpiry, bool shouldRefresh)
    {
        var user = await AddUserAsync("alice", "old-token");
        user.YotoTokenExpiresAt = minutesUntilExpiry is { } m ? DateTimeOffset.UtcNow.AddMinutes(m) : null;
        await _db.SaveChangesAsync();
        _yotoService.Setup(s => s.RefreshTokenAsync("refresh-token", It.IsAny<CancellationToken>())).ReturnsAsync(Refreshed);
        _yotoService.Setup(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _yotoService.Verify(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), shouldRefresh ? Times.Once() : Times.Never());
        var expectedToken = shouldRefresh ? "refreshed-token" : "old-token";
        _yotoService.Verify(s => s.GetUserCardsAsync(expectedToken, It.IsAny<CancellationToken>()), Times.Once);
        (await _db.UserConnections.SingleAsync()).YotoAccessToken.Should().Be(expectedToken);
    }

    [Fact]
    public async Task GetCards_WhenTheRefreshFails_Returns401AndDoesNotCallYoto()
    {
        var user = await AddUserAsync("alice");
        user.YotoTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _db.SaveChangesAsync();
        _yotoService.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("revoked"));

        var result = await _sut.AsUser(user.Id).GetCards(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Verify(s => s.GetUserCardsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- single card and delete: the same guard, the caller's own token

    [Fact]
    public async Task GetCard_UserNotFound_Returns401AndDoesNotCallYoto()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).GetCard("card-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCard_NoYotoConnection_Returns401AndDoesNotCallYoto()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null, yotoRefreshToken: null);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var result = await _sut.AsUser(user.Id).GetCard("card-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCard_FetchesThatCardWithTheCallersToken()
    {
        var user = await AddUserAsync("alice", "alice-token");
        var card = CardWithChapters("card-1", 1);
        _yotoService.Setup(s => s.GetCardContentAsync("alice-token", "card-1", It.IsAny<CancellationToken>())).ReturnsAsync(card);

        var result = await _sut.AsUser(user.Id).GetCard("card-1", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(card);
    }

    [Fact]
    public async Task GetCard_WhenTheRefreshFails_Returns401()
    {
        var user = await AddUserAsync("alice");
        user.YotoTokenExpiresAt = null;
        await _db.SaveChangesAsync();
        _yotoService.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());

        var result = await _sut.AsUser(user.Id).GetCard("card-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Verify(s => s.GetCardContentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteCard_UserNotFound_Returns401AndDeletesNothing()
    {
        var result = await _sut.AsUser(Guid.NewGuid()).DeleteCard("card-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCard_WhenTheRefreshFails_Returns401AndDeletesNothing()
    {
        var user = await AddUserAsync("alice");
        user.YotoTokenExpiresAt = null;
        await _db.SaveChangesAsync();
        _yotoService.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());

        var result = await _sut.AsUser(user.Id).DeleteCard("card-1", CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        _yotoService.Verify(s => s.DeleteCardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
