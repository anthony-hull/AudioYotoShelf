using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class PlaylistServiceTests : IDisposable
{
    private readonly InMemoryDbFixture _fixture;
    private readonly Mock<IAudiobookshelfService> _absService;
    private readonly PlaylistService _sut;

    public PlaylistServiceTests()
    {
        _fixture = new InMemoryDbFixture();
        _absService = new Mock<IAudiobookshelfService>();
        var limits = new YotoCardLimits();
        _sut = new PlaylistService(
            _fixture.DbContext, _absService.Object,
            new TrackPlanner(limits), new CardCapacityCalculator(limits),
            Mock.Of<ILogger<PlaylistService>>());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<Guid> SeedUserAsync()
    {
        var user = TestData.CreateUserConnection();
        _fixture.DbContext.UserConnections.Add(user);
        await _fixture.DbContext.SaveChangesAsync();
        return user.Id;
    }

    private void SetupBook(string itemId, AbsBookMedia media) =>
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem(itemId, media));

    [Fact]
    public async Task Create_DefaultsToDraftAndAutoGrouping()
    {
        var userId = await SeedUserAsync();

        var result = await _sut.CreateAsync(userId, new CreatePlaylistRequest("My Mix"));

        result.Name.Should().Be("My Mix");
        result.Status.Should().Be(PlaylistStatus.Draft);
        result.DefaultGrouping.Should().Be(TrackGrouping.Auto);
        result.Capacity.WithinLimits.Should().BeTrue();
    }

    [Fact]
    public async Task AddItems_CachesTracksAndComputesCapacity()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));

        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 600, size: 10_000_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 600, size: 10_000_000),
        ]));

        var result = await _sut.AddItemsAsync(playlist.Id, ["book-1"]);

        result.Items.Should().HaveCount(1);
        result.Items[0].ProjectedTrackCount.Should().Be(2);
        result.Capacity.Tracks.Should().Be(2);
        result.Capacity.WithinLimits.Should().BeTrue();
    }

    [Fact]
    public async Task AddItems_DuplicateBook_IsIgnored()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        SetupBook("book-1", TestData.CreateAbsMedia());

        await _sut.AddItemsAsync(playlist.Id, ["book-1"]);
        var result = await _sut.AddItemsAsync(playlist.Id, ["book-1"]);

        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Capacity_FlagsOverflowAndFirstBook_WhenTooLong()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));

        // One 6-hour book (single track) exceeds the 5h total limit.
        SetupBook("big-book", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 21600, size: 50_000_000)],
            chapters: []));

        var result = await _sut.AddItemsAsync(playlist.Id, ["big-book"]);

        result.Capacity.WithinLimits.Should().BeFalse();
        result.Capacity.ExceedsDuration.Should().BeTrue();
        result.Capacity.FirstOverflowItemId.Should().Be("big-book");
    }

    [Fact]
    public async Task Reorder_UpdatesPositions()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        SetupBook("a", TestData.CreateAbsMedia());
        SetupBook("b", TestData.CreateAbsMedia());
        var withItems = await _sut.AddItemsAsync(playlist.Id, ["a", "b"]);
        var idA = withItems.Items.Single(i => i.AbsLibraryItemId == "a").Id;
        var idB = withItems.Items.Single(i => i.AbsLibraryItemId == "b").Id;

        var result = await _sut.ReorderAsync(playlist.Id, [idB, idA]);

        result.Items.Select(i => i.AbsLibraryItemId).Should().Equal("b", "a");
    }

    [Fact]
    public async Task SetItemGrouping_OverridesProjection()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 300, size: 5_000_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 300, size: 5_000_000),
        ]));
        var added = await _sut.AddItemsAsync(playlist.Id, ["book-1"]);
        added.Items[0].ProjectedTrackCount.Should().Be(2);

        var result = await _sut.SetItemGroupingAsync(playlist.Id, added.Items[0].Id, TrackGrouping.SingleTrack);

        result.Items[0].EffectiveGrouping.Should().Be(TrackGrouping.SingleTrack);
        result.Items[0].ProjectedTrackCount.Should().Be(1); // merged into one track (within 1h)
    }

    // =========================================================================
    // Mutation-testing additions
    // =========================================================================

    private async Task<Guid> SeedUserAsync(Action<UserConnection> configure)
    {
        var user = TestData.CreateUserConnection();
        configure(user);
        _fixture.DbContext.UserConnections.Add(user);
        await _fixture.DbContext.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Reads the playlist back through a second context, i.e. what was really saved.</summary>
    private async Task<Playlist> StoredPlaylistAsync(Guid playlistId)
    {
        await using var other = _fixture.NewContext();
        return await other.Playlists.Include(p => p.Items).SingleAsync(p => p.Id == playlistId);
    }

    /// <summary>Adds books "a".."d" (or the given ids) to a fresh playlist and returns their item ids by book id.</summary>
    private async Task<(Guid PlaylistId, Dictionary<string, Guid> ItemIds)> SeedPlaylistWithBooksAsync(params string[] bookIds)
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));
        foreach (var id in bookIds) SetupBook(id, TestData.CreateAbsMedia());
        var withItems = await _sut.AddItemsAsync(playlist.Id, bookIds);
        return (playlist.Id, withItems.Items.ToDictionary(i => i.AbsLibraryItemId, i => i.Id));
    }

    // --- Create / Get / Update / Delete / List

    [Fact]
    public async Task Create_UnknownUser_Throws()
    {
        var act = () => _sut.CreateAsync(Guid.NewGuid(), new CreatePlaylistRequest("Mix"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("User connection not found");
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersPlaylists_NewestFirst()
    {
        var mine = await SeedUserAsync();
        var someoneElse = await SeedUserAsync();
        var now = DateTimeOffset.UtcNow;
        _fixture.DbContext.Playlists.AddRange(
            new Playlist { Name = "old", UserConnectionId = mine, CreatedAt = now.AddHours(-2) },
            new Playlist { Name = "new", UserConnectionId = mine, CreatedAt = now },
            new Playlist { Name = "not mine", UserConnectionId = someoneElse, CreatedAt = now.AddHours(-1) });
        await _fixture.DbContext.SaveChangesAsync();

        var result = await _sut.ListAsync(mine);

        result.Select(p => p.Name).Should().Equal("new", "old");
    }

    [Fact]
    public async Task List_ReportsTheNumberOfItemsInEachPlaylist()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync("a", "b");
        var owner = (await StoredPlaylistAsync(playlistId)).UserConnectionId;

        (await _sut.ListAsync(owner)).Single().ItemCount.Should().Be(2);
    }

    [Fact]
    public async Task Get_UnknownPlaylist_ReturnsNull()
    {
        (await _sut.GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Get_ExistingPlaylist_ReturnsIt()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync("a");

        var result = await _sut.GetAsync(playlistId);

        result!.Id.Should().Be(playlistId);
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_NameOnly_ChangesTheNameAndKeepsTheGrouping_AndSaves()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync();

        var result = await _sut.UpdateAsync(playlistId, new UpdatePlaylistRequest(Name: "Renamed"));

        (result.Name, result.DefaultGrouping).Should().Be(("Renamed", TrackGrouping.Chapters));
        var stored = await StoredPlaylistAsync(playlistId);
        (stored.Name, stored.DefaultGrouping).Should().Be(("Renamed", TrackGrouping.Chapters));
    }

    [Fact]
    public async Task Update_GroupingOnly_ChangesTheGroupingAndKeepsTheName()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync();

        var result = await _sut.UpdateAsync(playlistId, new UpdatePlaylistRequest(DefaultGrouping: TrackGrouping.SingleTrack));

        (result.Name, result.DefaultGrouping).Should().Be(("Mix", TrackGrouping.SingleTrack));
        (await StoredPlaylistAsync(playlistId)).DefaultGrouping.Should().Be(TrackGrouping.SingleTrack);
    }

    [Fact]
    public async Task Update_UnknownPlaylist_Throws()
    {
        var act = () => _sut.UpdateAsync(Guid.NewGuid(), new UpdatePlaylistRequest(Name: "x"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Playlist not found");
    }

    [Fact]
    public async Task Delete_RemovesThePlaylistFromTheStore()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync();

        await _sut.DeleteAsync(playlistId);

        await using var other = _fixture.NewContext();
        (await other.Playlists.AnyAsync(p => p.Id == playlistId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_UnknownPlaylist_Throws()
    {
        var act = () => _sut.DeleteAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Playlist not found");
    }

    // --- AddItems

    [Fact]
    public async Task AddItems_ContinuesAfterTheHighestExistingPosition()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync();
        _fixture.DbContext.PlaylistItems.AddRange(
            new PlaylistItem { PlaylistId = playlistId, AbsLibraryItemId = "old-1", BookTitle = "Old 1", Position = 0 },
            new PlaylistItem { PlaylistId = playlistId, AbsLibraryItemId = "old-2", BookTitle = "Old 2", Position = 5 });
        await _fixture.DbContext.SaveChangesAsync();
        SetupBook("new-1", TestData.CreateAbsMedia());
        SetupBook("new-2", TestData.CreateAbsMedia());

        var result = await _sut.AddItemsAsync(playlistId, ["new-1", "new-2"]);

        result.Items.Where(i => i.AbsLibraryItemId.StartsWith("new")).Select(i => i.Position).Should().Equal(6, 7);
    }

    [Fact]
    public async Task AddItems_ToAnEmptyPlaylist_StartAtPositionZero()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync("a", "b", "c");

        (await _sut.GetAsync(playlistId))!.Items.Select(i => i.Position).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task AddItems_SavesTheItemsWithTheirCachedTracks()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 100, size: 1_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 200, size: 2_000),
            TestData.CreateAbsAudioFile(2, "ino-3", duration: 300, size: 3_000),
        ], chapters: []));

        var result = await _sut.AddItemsAsync(playlist.Id, ["book-1"]);

        var stored = (await StoredPlaylistAsync(playlist.Id)).Items.Single();
        stored.TrackDurations.Should().Equal(100.0, 200.0, 300.0);
        stored.TrackBytes.Should().Equal(1_000L, 2_000L, 3_000L);
        // The response sums the cached tracks rather than taking the largest one.
        (result.Items[0].DurationSeconds, result.Items[0].EstimatedBytes, result.Items[0].ProjectedTrackCount)
            .Should().Be((600.0, 6_000L, 3));
    }

    [Fact]
    public async Task AddItems_RecordsTitleAuthorAndSeries()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        SetupBook("b1", TestData.CreateAbsMedia(TestData.CreateAbsMetadata("Real Title", seriesName: "Saga", seriesSequence: "2.5")));

        var result = await _sut.AddItemsAsync(playlist.Id, ["b1"]);

        var item = result.Items.Single();
        (item.BookTitle, item.BookAuthor, item.SeriesName, item.SeriesSequence)
            .Should().Be(("Real Title", "Test Author", "Saga", 2.5f));
    }

    [Fact]
    public async Task AddItems_BookWithoutTitleAuthorsOrSeries_UsesUnknownAndLeavesTheRestEmpty()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        var bare = TestData.CreateAbsMetadata() with { Title = null, Authors = [], Series = [] };
        SetupBook("b1", TestData.CreateAbsMedia(bare));

        var item = (await _sut.AddItemsAsync(playlist.Id, ["b1"])).Items.Single();

        (item.BookTitle, item.BookAuthor, item.SeriesName, item.SeriesSequence).Should().Be(("Unknown", null, null, null));
    }

    [Fact]
    public async Task AddItems_BookWithNoMedia_IsSkipped()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), "no-media", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem("no-media") with { Media = null });
        SetupBook("ok", TestData.CreateAbsMedia());

        var result = await _sut.AddItemsAsync(playlist.Id, ["no-media", "ok"]);

        result.Items.Select(i => (i.AbsLibraryItemId, i.Position)).Should().Equal(("ok", 0));
    }

    [Fact]
    public async Task AddItems_UnknownPlaylist_Throws()
    {
        var act = () => _sut.AddItemsAsync(Guid.NewGuid(), ["a"]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Playlist not found");
    }

    [Fact]
    public async Task AddItems_PlaylistOwnerNoLongerExists_Throws()
    {
        _fixture.DbContext.Playlists.Add(new Playlist { Name = "Orphan", UserConnectionId = Guid.NewGuid() });
        await _fixture.DbContext.SaveChangesAsync();
        var orphan = await _fixture.DbContext.Playlists.SingleAsync();

        var act = () => _sut.AddItemsAsync(orphan.Id, ["a"]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("User connection not found");
    }

    [Fact]
    public async Task AddItems_OwnerWithoutAValidAudiobookshelfConnection_Throws()
    {
        var userId = await SeedUserAsync(u => u.AudiobookshelfTokenValidatedAt = null);
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));

        var act = () => _sut.AddItemsAsync(playlist.Id, ["a"]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No valid Audiobookshelf connection");
    }

    [Fact]
    public async Task AddItems_RefreshesAnExpiringAudiobookshelfTokenBeforeFetchingBooks()
    {
        var userId = await SeedUserAsync(u =>
        {
            u.AudiobookshelfToken = "stale";
            u.AudiobookshelfRefreshToken = "refresh-old";
            u.AudiobookshelfTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        });
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));
        _absService.Setup(s => s.RefreshTokenAsync("http://abs.local", "refresh-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLoginResponse(
                new AbsUser("u1", "testuser", "user", "legacy", true, null, null, "renewed", "refresh-new"), null));
        SetupBook("b1", TestData.CreateAbsMedia());

        await _sut.AddItemsAsync(playlist.Id, ["b1"]);

        _absService.Verify(s => s.GetLibraryItemAsync("http://abs.local", "renewed", "b1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("1.5", 1.5f)]
    [InlineData("10", 10f)]
    [InlineData("abc", null)]
    [InlineData("1,5", null)]     // not a number in the invariant culture
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseSequence_ReadsInvariantNumbersAndNothingElse(string? sequence, float? expected)
    {
        PlaylistService.ParseSequence(sequence).Should().Be(expected);
    }

    // --- AddSeries

    private void SetupSeries(params (string BookId, string? Sequence)[] books) =>
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-1", "series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsSeriesItem("series-1", "Saga", null,
                books.Select(b => new AbsSeriesBook(b.BookId, null, b.Sequence)).ToArray(), 0));

    [Fact]
    public async Task AddSeries_UserWithoutADefaultLibrary_Throws()
    {
        var userId = await SeedUserAsync();
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix"));

        var act = () => _sut.AddSeriesAsync(playlist.Id, "series-1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No default library set*");
    }

    [Theory]
    [InlineData("", "Saga", "Saga")]      // unnamed playlist takes the series name
    [InlineData("  ", "Saga", "Saga")]
    [InlineData("Mine", "Saga", "Mine")]  // a named playlist keeps its name
    public async Task AddSeries_NamesAnUnnamedPlaylistAfterTheSeriesOnly(string playlistName, string seriesName, string expected)
    {
        var userId = await SeedUserAsync(u => u.DefaultLibraryId = "lib-1");
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest(playlistName));
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-1", "series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsSeriesItem("series-1", seriesName, null, [], 0));

        var result = await _sut.AddSeriesAsync(playlist.Id, "series-1");

        result.Name.Should().Be(expected);
    }

    [Fact]
    public async Task AddSeries_SeriesWithNoName_LeavesAnUnnamedPlaylistUnnamed()
    {
        var userId = await SeedUserAsync(u => u.DefaultLibraryId = "lib-1");
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest(""));
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), "lib-1", "series-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsSeriesItem("series-1", " ", null, [], 0));

        (await _sut.AddSeriesAsync(playlist.Id, "series-1")).Name.Should().Be("");
    }

    [Fact]
    public async Task AddSeries_AddsBooksInSeriesOrder_WithUnnumberedBooksLast()
    {
        var userId = await SeedUserAsync(u => u.DefaultLibraryId = "lib-1");
        var playlist = await _sut.CreateAsync(userId, new CreatePlaylistRequest("Mix", TrackGrouping.Chapters));
        SetupSeries(("ten", "10"), ("two", "2"), ("none", null), ("one", "1"));
        foreach (var id in new[] { "ten", "two", "none", "one" }) SetupBook(id, TestData.CreateAbsMedia());

        var result = await _sut.AddSeriesAsync(playlist.Id, "series-1");

        result.Items.Select(i => i.AbsLibraryItemId).Should().Equal("one", "two", "ten", "none");
    }

    // --- RemoveItem / Reorder / SetItemGrouping

    [Fact]
    public async Task RemoveItem_DeletesItAndCloseTheGapInPositions()
    {
        var (playlistId, ids) = await SeedPlaylistWithBooksAsync("a", "b", "c");

        var result = await _sut.RemoveItemAsync(playlistId, ids["b"]);

        result.Items.Select(i => (i.AbsLibraryItemId, i.Position)).Should().Equal(("a", 0), ("c", 1));
        var stored = (await StoredPlaylistAsync(playlistId)).Items.OrderBy(i => i.Position);
        stored.Select(i => (i.AbsLibraryItemId, i.Position)).Should().Equal(("a", 0), ("c", 1));
    }

    [Fact]
    public async Task RemoveItem_UnknownItem_ChangesNothing()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync("a", "b");

        var result = await _sut.RemoveItemAsync(playlistId, Guid.NewGuid());

        result.Items.Select(i => i.AbsLibraryItemId).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Reorder_ItemsNotNamed_FollowInTheirExistingOrder_AndTheOrderIsSaved()
    {
        var (playlistId, ids) = await SeedPlaylistWithBooksAsync("a", "b", "c", "d");

        var result = await _sut.ReorderAsync(playlistId, [ids["c"]]);

        result.Items.Select(i => (i.AbsLibraryItemId, i.Position)).Should().Equal(("c", 0), ("a", 1), ("b", 2), ("d", 3));
        var stored = (await StoredPlaylistAsync(playlistId)).Items.OrderBy(i => i.Position);
        stored.Select(i => i.AbsLibraryItemId).Should().Equal("c", "a", "b", "d");
    }

    [Fact]
    public async Task Reorder_IgnoresIdsThatAreNotInThePlaylist()
    {
        var (playlistId, ids) = await SeedPlaylistWithBooksAsync("a", "b");

        var result = await _sut.ReorderAsync(playlistId, [Guid.NewGuid(), ids["b"], ids["a"]]);

        result.Items.Select(i => (i.AbsLibraryItemId, i.Position)).Should().Equal(("b", 0), ("a", 1));
    }

    [Fact]
    public async Task SetItemGrouping_IsSaved_AndCanBeCleared()
    {
        var (playlistId, ids) = await SeedPlaylistWithBooksAsync("a");

        await _sut.SetItemGroupingAsync(playlistId, ids["a"], TrackGrouping.SingleTrack);
        (await StoredPlaylistAsync(playlistId)).Items.Single().GroupingOverride.Should().Be(TrackGrouping.SingleTrack);

        await _sut.SetItemGroupingAsync(playlistId, ids["a"], null);
        (await StoredPlaylistAsync(playlistId)).Items.Single().GroupingOverride.Should().BeNull();
    }

    [Fact]
    public async Task SetItemGrouping_UnknownItem_Throws()
    {
        var (playlistId, _) = await SeedPlaylistWithBooksAsync("a");

        var act = () => _sut.SetItemGroupingAsync(playlistId, Guid.NewGuid(), TrackGrouping.Auto);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Playlist item not found");
    }
}
