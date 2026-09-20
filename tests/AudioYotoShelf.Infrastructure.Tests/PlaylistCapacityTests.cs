using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Infrastructure.Services;
using FluentAssertions;

namespace AudioYotoShelf.Infrastructure.Tests;

public class PlaylistCapacityTests
{
    private readonly TrackPlanner _planner = new(new YotoCardLimits());

    private static PlaylistItem Item(string id, int position, List<double> durations, List<long> bytes,
        TrackGrouping? groupingOverride = null) =>
        new()
        {
            AbsLibraryItemId = id,
            BookTitle = $"Title {id}",
            Position = position,
            TrackDurations = durations,
            TrackBytes = bytes,
            GroupingOverride = groupingOverride,
        };

    private static Playlist PlaylistOf(TrackGrouping grouping, params PlaylistItem[] items) =>
        new() { Name = "Mix", DefaultGrouping = grouping, Items = items };

    [Fact]
    public void BuildPlans_OrdersBooksByPosition_NotByInsertionOrder()
    {
        var playlist = PlaylistOf(TrackGrouping.Chapters,
            Item("second", 1, [60], [1_000]),
            Item("first", 0, [60], [1_000]),
            Item("third", 2, [60], [1_000]));

        var plans = PlaylistCapacity.BuildPlans(playlist, _planner);

        plans.Select(p => p.AbsLibraryItemId).Should().Equal("first", "second", "third");
        plans[0].BookTitle.Should().Be("Title first");
    }

    [Fact]
    public void ChapterTracks_PairsEachDurationWithItsByteSize_AndTitlesThemWithTheBook()
    {
        var item = Item("b1", 0, [10, 20, 30], [100, 200, 300]);

        var tracks = PlaylistCapacity.ChapterTracks(item);

        tracks.Select(t => (t.Title, t.DurationSeconds, t.EstimatedBytes)).Should().Equal(
            ("Title b1", 10.0, 100L), ("Title b1", 20.0, 200L), ("Title b1", 30.0, 300L));
    }

    [Fact]
    public void ChapterTracks_FewerByteSizesThanDurations_TreatsTheMissingOnesAsZero()
    {
        var item = Item("b1", 0, [10, 20, 30], [100, 200]);

        PlaylistCapacity.ChapterTracks(item).Select(t => t.EstimatedBytes).Should().Equal(100L, 200L, 0L);
    }

    [Fact]
    public void GroupedTracks_ItemOverrideWinsOverThePlaylistDefault()
    {
        var overridden = Item("b1", 0, [60, 60], [1_000, 1_000], TrackGrouping.SingleTrack);
        var inherited = Item("b2", 1, [60, 60], [1_000, 1_000]);
        var playlist = PlaylistOf(TrackGrouping.Chapters, overridden, inherited);

        PlaylistCapacity.GroupedTracks(playlist, overridden, _planner).Should().HaveCount(1);
        PlaylistCapacity.GroupedTracks(playlist, inherited, _planner).Should().HaveCount(2);
    }
}
