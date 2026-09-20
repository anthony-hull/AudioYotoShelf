using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
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

public class PlaylistsControllerTests : IDisposable
{
    private readonly AudioYotoShelfDbContext _db;
    private readonly Mock<IPlaylistService> _playlists = new();
    private readonly Mock<IBackgroundJobClient> _backgroundJobs = new();
    private readonly PlaylistsController _sut;

    public PlaylistsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AudioYotoShelfDbContext>()
            .UseInMemoryDatabase($"PlaylistsCtrlTest_{Guid.NewGuid()}")
            .Options;
        _db = new AudioYotoShelfDbContext(options);
        _backgroundJobs
            .Setup(b => b.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()))
            .Returns("job-7");

        _sut = new PlaylistsController(
            _playlists.Object, _backgroundJobs.Object, _db, Mock.Of<ILogger<PlaylistsController>>());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task<Guid> AddPlaylistOwnedByAsync(Guid ownerId)
    {
        var playlist = new Playlist { UserConnectionId = ownerId, Name = "Bedtime" };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();
        return playlist.Id;
    }

    private static PlaylistResponse Response(Guid id, bool withinLimits = true) => new(
        id, "Bedtime", PlaylistStatus.Draft, TrackGrouping.Auto, null, null,
        DateTimeOffset.UnixEpoch, null, [],
        new CapacitySummary(3, 100, 600, 18000, 1_000, 500L * 1024 * 1024, withinLimits, false, false, false, null, []));

    // =========================================================================
    // Ownership: every action on one playlist answers 404 for someone else's, and does nothing
    // =========================================================================

    public static TheoryData<string, Func<PlaylistsController, Guid, Task<IActionResult>>> ActionsOnOnePlaylist() => new()
    {
        { "GetDetail", (c, id) => c.GetDetail(id, default) },
        { "Update", (c, id) => c.Update(id, new UpdatePlaylistRequest("New"), default) },
        { "Delete", (c, id) => c.Delete(id, default) },
        { "AddItems", (c, id) => c.AddItems(id, new AddPlaylistItemsRequest(["item-1"]), default) },
        { "AddSeries", (c, id) => c.AddSeries(id, new AddPlaylistSeriesRequest("ser-1"), default) },
        { "RemoveItem", (c, id) => c.RemoveItem(id, Guid.NewGuid(), default) },
        { "Reorder", (c, id) => c.Reorder(id, new ReorderPlaylistRequest([Guid.NewGuid()]), default) },
        { "SetItemGrouping", (c, id) => c.SetItemGrouping(id, Guid.NewGuid(), new UpdatePlaylistItemRequest(TrackGrouping.SingleTrack), default) },
        { "Transfer", (c, id) => c.Transfer(id, default) },
    };

    [Theory]
    [MemberData(nameof(ActionsOnOnePlaylist))]
    public async Task Action_OnAnotherUsersPlaylist_Returns404AndTouchesNothing(
        string action, Func<PlaylistsController, Guid, Task<IActionResult>> call)
    {
        var playlistId = await AddPlaylistOwnedByAsync(Guid.NewGuid());

        var result = await call(_sut.AsUser(Guid.NewGuid()), playlistId);

        result.Should().BeOfType<NotFoundResult>($"{action} must not act on a playlist owned by someone else");
        _playlists.Invocations.Should().BeEmpty();
        _backgroundJobs.VerifyNothingEnqueued();
    }

    [Theory]
    [MemberData(nameof(ActionsOnOnePlaylist))]
    public async Task Action_OnAPlaylistThatDoesNotExist_Returns404AndTouchesNothing(
        string action, Func<PlaylistsController, Guid, Task<IActionResult>> call)
    {
        var result = await call(_sut.AsUser(Guid.NewGuid()), Guid.NewGuid());

        result.Should().BeOfType<NotFoundResult>($"{action} on a missing playlist");
        _playlists.Invocations.Should().BeEmpty();
    }

    // =========================================================================
    // The caller's own playlist: each action passes the right arguments through
    // =========================================================================

    [Fact]
    public async Task Create_CreatesForTheCallerAndPointsAtTheNewPlaylist()
    {
        var caller = Guid.NewGuid();
        var request = new CreatePlaylistRequest("Bedtime");
        var created = Response(Guid.NewGuid());
        _playlists.Setup(p => p.CreateAsync(caller, request, It.IsAny<CancellationToken>())).ReturnsAsync(created);

        var result = await _sut.AsUser(caller).Create(request, default);

        var createdAt = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdAt.ActionName.Should().Be(nameof(PlaylistsController.GetDetail));
        createdAt.RouteValues!["playlistId"].Should().Be(created.Id);
        createdAt.Value.Should().BeSameAs(created);
    }

    [Fact]
    public async Task List_ReturnsTheCallersPlaylists()
    {
        var caller = Guid.NewGuid();
        PlaylistSummaryResponse[] mine = [new(Guid.NewGuid(), "Mine", PlaylistStatus.Draft, 2, null, DateTimeOffset.UnixEpoch)];
        _playlists.Setup(p => p.ListAsync(caller, It.IsAny<CancellationToken>())).ReturnsAsync(mine);

        var result = await _sut.AsUser(caller).List(default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(mine);
    }

    [Fact]
    public async Task GetDetail_Owned_ReturnsThePlaylist()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        var response = Response(id);
        _playlists.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).GetDetail(id, default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task GetDetail_OwnedButGoneFromTheService_Returns404()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        _playlists.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((PlaylistResponse?)null);

        var result = await _sut.AsUser(caller).GetDetail(id, default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_Owned_PassesTheChangesThrough()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        var request = new UpdatePlaylistRequest("Renamed", TrackGrouping.SingleTrack);
        var response = Response(id);
        _playlists.Setup(p => p.UpdateAsync(id, request, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).Update(id, request, default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task Delete_Owned_DeletesThatPlaylist()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);

        var result = await _sut.AsUser(caller).Delete(id, default);

        result.Should().BeOfType<OkObjectResult>();
        _playlists.Verify(p => p.DeleteAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddItems_Owned_AddsTheRequestedBooks()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        string[] items = ["item-1", "item-2"];
        var response = Response(id);
        _playlists.Setup(p => p.AddItemsAsync(id, items, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).AddItems(id, new AddPlaylistItemsRequest(items), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task AddSeries_Owned_AddsThatSeries()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        var response = Response(id);
        _playlists.Setup(p => p.AddSeriesAsync(id, "ser-1", It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).AddSeries(id, new AddPlaylistSeriesRequest("ser-1"), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task RemoveItem_Owned_RemovesThatItem()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        var itemId = Guid.NewGuid();
        var response = Response(id);
        _playlists.Setup(p => p.RemoveItemAsync(id, itemId, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).RemoveItem(id, itemId, default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task Reorder_Owned_AppliesTheRequestedOrder()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        Guid[] order = [Guid.NewGuid(), Guid.NewGuid()];
        var response = Response(id);
        _playlists.Setup(p => p.ReorderAsync(id, order, It.IsAny<CancellationToken>())).ReturnsAsync(response);

        var result = await _sut.AsUser(caller).Reorder(id, new ReorderPlaylistRequest(order), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task SetItemGrouping_Owned_SetsTheOverrideOnThatItem()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        var itemId = Guid.NewGuid();
        var response = Response(id);
        _playlists.Setup(p => p.SetItemGroupingAsync(id, itemId, TrackGrouping.SingleTrack, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _sut.AsUser(caller).SetItemGrouping(id, itemId, new UpdatePlaylistItemRequest(TrackGrouping.SingleTrack), default);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    // =========================================================================
    // Transfer: blocked when it will not fit one card
    // =========================================================================

    [Fact]
    public async Task Transfer_WithinCapacity_QueuesThePlaylistTransfer()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        _playlists.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Response(id, withinLimits: true));

        var result = await _sut.AsUser(caller).Transfer(id, default);

        result.Should().BeOfType<AcceptedResult>();
        _backgroundJobs.VerifyEnqueued<IPlaylistJobService>("ExecutePlaylistTransferAsync", args => (Guid)args[0] == id);
    }

    [Fact]
    public async Task Transfer_OverCapacity_Returns409AndQueuesNothing()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        _playlists.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Response(id, withinLimits: false));

        var result = await _sut.AsUser(caller).Transfer(id, default);

        result.Should().BeOfType<ConflictObjectResult>();
        _backgroundJobs.VerifyNothingEnqueued();
    }

    [Fact]
    public async Task Transfer_OwnedButGoneFromTheService_Returns404AndQueuesNothing()
    {
        var caller = Guid.NewGuid();
        var id = await AddPlaylistOwnedByAsync(caller);
        _playlists.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((PlaylistResponse?)null);

        var result = await _sut.AsUser(caller).Transfer(id, default);

        result.Should().BeOfType<NotFoundResult>();
        _backgroundJobs.VerifyNothingEnqueued();
    }
}
