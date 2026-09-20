using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.Services;
using FluentAssertions;

namespace AudioYotoShelf.Core.Tests;

public class CardCapacityCalculatorTests
{
    private readonly YotoCardLimits _limits = new();
    private readonly CardCapacityCalculator _sut;

    public CardCapacityCalculatorTests() => _sut = new CardCapacityCalculator(_limits);

    private static BookTrackPlan Book(string id, params SourceTrack[] tracks) => new(id, id, tracks);

    // =========================================================================
    // ProjectedTrackCount — splitting math
    // =========================================================================

    [Theory]
    [InlineData(3600, 1)]    // exactly 1 hour -> 1 track
    [InlineData(3601, 2)]    // just over 1 hour -> 2 tracks
    [InlineData(5400, 2)]    // 90 min -> 2 tracks
    [InlineData(9000, 3)]    // 2.5 h -> 3 tracks
    [InlineData(60, 1)]      // short -> 1 track
    public void ProjectedTrackCount_SplitsByDuration(double seconds, int expected)
    {
        _sut.ProjectedTrackCount(new SourceTrack("t", seconds, 0)).Should().Be(expected);
    }

    [Fact]
    public void ProjectedTrackCount_SplitsByBytes()
    {
        var track = new SourceTrack("t", 60, 250L * 1024 * 1024); // 250 MB, 1 min
        _sut.ProjectedTrackCount(track).Should().Be(3);           // ceil(250/100)
    }

    [Fact]
    public void ProjectedTrackCount_NeverBelowOne()
    {
        _sut.ProjectedTrackCount(new SourceTrack("t", 0, 0)).Should().Be(1);
    }

    // =========================================================================
    // Calculate — aggregate limits
    // =========================================================================

    [Fact]
    public void Calculate_Empty_IsWithinLimits()
    {
        var result = _sut.Calculate([]);

        result.WithinLimits.Should().BeTrue();
        result.Projected.Should().Be(new CapacityUsage(0, 0, 0));
        result.FirstOverflowItemId.Should().BeNull();
    }

    [Fact]
    public void Calculate_SmallBook_IsWithinLimits()
    {
        var result = _sut.Calculate([Book("b1",
            new SourceTrack("c1", 600, 10_000_000),
            new SourceTrack("c2", 600, 10_000_000))]);

        result.WithinLimits.Should().BeTrue();
        result.Projected.Tracks.Should().Be(2);
        result.ExceedsTracks.Should().BeFalse();
    }

    [Fact]
    public void Calculate_TooManyTracks_FlagsTracksOnly()
    {
        var tracks = Enumerable.Range(0, 101)
            .Select(i => new SourceTrack($"c{i}", 60, 1_000_000))
            .ToArray();

        var result = _sut.Calculate([Book("b1", tracks)]);

        result.WithinLimits.Should().BeFalse();
        result.ExceedsTracks.Should().BeTrue();
        result.ExceedsDuration.Should().BeFalse();
        result.ExceedsBytes.Should().BeFalse();
    }

    [Fact]
    public void Calculate_TooLong_FlagsDuration()
    {
        // 6 one-hour tracks = 6h > 5h limit; each is exactly 1h so no split, 6 tracks total.
        var tracks = Enumerable.Range(0, 6)
            .Select(i => new SourceTrack($"c{i}", 3600, 1_000_000))
            .ToArray();

        var result = _sut.Calculate([Book("b1", tracks)]);

        result.ExceedsDuration.Should().BeTrue();
        result.ExceedsTracks.Should().BeFalse();
        result.Projected.Tracks.Should().Be(6);
    }

    [Fact]
    public void Calculate_TooLarge_FlagsBytes()
    {
        var result = _sut.Calculate([Book("b1",
            new SourceTrack("c1", 60, 600L * 1024 * 1024))]); // 600 MB

        result.ExceedsBytes.Should().BeTrue();
        result.ExceedsDuration.Should().BeFalse();
    }

    [Fact]
    public void Calculate_ExactlyAtLimits_IsWithin()
    {
        // 5 tracks of exactly 1h = 5h, 100 MB each = 500 MB. All exactly at the line.
        var tracks = Enumerable.Range(0, 5)
            .Select(i => new SourceTrack($"c{i}", 3600, 100L * 1024 * 1024))
            .ToArray();

        var result = _sut.Calculate([Book("b1", tracks)]);

        result.WithinLimits.Should().BeTrue();
        result.Projected.Tracks.Should().Be(5);
    }

    [Fact]
    public void Calculate_ReportsFirstBookThatOverflows()
    {
        var fits = Book("book-1", new SourceTrack("c", 3600, 1_000_000));      // 1h
        var overflows = Book("book-2",
            Enumerable.Range(0, 5).Select(i => new SourceTrack($"c{i}", 3600, 1_000_000)).ToArray()); // +5h

        var result = _sut.Calculate([fits, overflows]);

        result.ExceedsDuration.Should().BeTrue();
        result.FirstOverflowItemId.Should().Be("book-2");
    }

    // =========================================================================
    // Mutation-testing additions — exact boundaries and the user-facing messages
    // =========================================================================

    private static SourceTrack[] Tracks(int count, double seconds, long bytes) =>
        Enumerable.Range(0, count).Select(i => new SourceTrack($"c{i}", seconds, bytes)).ToArray();

    [Fact]
    public void Calculate_ExactlyMaxTracks_DoesNotFlagAnyBookAsOverflow()
    {
        var result = _sut.Calculate([Book("b1", Tracks(100, 60, 1_000_000))]);

        result.ExceedsTracks.Should().BeFalse();
        result.FirstOverflowItemId.Should().BeNull();
    }

    [Fact]
    public void Calculate_ExactlyMaxDuration_DoesNotFlagAnyBookAsOverflow()
    {
        var result = _sut.Calculate([Book("b1", Tracks(5, 3600, 1_000_000))]); // 5h exactly

        result.ExceedsDuration.Should().BeFalse();
        result.FirstOverflowItemId.Should().BeNull();
    }

    [Fact]
    public void Calculate_ExactlyMaxBytes_DoesNotFlagAnyBookAsOverflow()
    {
        var result = _sut.Calculate([Book("b1", Tracks(5, 60, 100L * 1024 * 1024))]); // 500 MB exactly

        result.ExceedsBytes.Should().BeFalse();
        result.FirstOverflowItemId.Should().BeNull();
    }

    [Fact]
    public void Calculate_OneTrackOverMax_FlagsTheBook()
    {
        var result = _sut.Calculate([Book("b1", Tracks(101, 60, 1_000_000))]);

        result.ExceedsTracks.Should().BeTrue();
        result.FirstOverflowItemId.Should().Be("b1");
    }

    [Fact]
    public void Calculate_OneSecondOverMaxDuration_FlagsTheBook()
    {
        var result = _sut.Calculate([Book("b1", [.. Tracks(5, 3600, 1_000_000), new SourceTrack("extra", 1, 1_000_000)])]);

        result.ExceedsDuration.Should().BeTrue();
        result.FirstOverflowItemId.Should().Be("b1");
    }

    [Fact]
    public void Calculate_OneByteOverMaxBytes_FlagsTheBook()
    {
        var result = _sut.Calculate([Book("b1", [.. Tracks(5, 60, 100L * 1024 * 1024), new SourceTrack("extra", 60, 1)])]);

        result.ExceedsBytes.Should().BeTrue();
        result.FirstOverflowItemId.Should().Be("b1");
    }

    [Fact]
    public void Calculate_SeveralBooksOverflow_ReportsTheFirstOne()
    {
        var fits = Book("fits", new SourceTrack("c", 3600, 1_000_000));
        var firstOver = Book("first-over", Tracks(5, 3600, 1_000_000));
        var secondOver = Book("second-over", new SourceTrack("c", 60, 1_000_000));

        _sut.Calculate([fits, firstOver, secondOver]).FirstOverflowItemId.Should().Be("first-over");
    }

    [Fact]
    public void Calculate_WithinLimits_HasNoMessages()
    {
        _sut.Calculate([Book("b1", Tracks(2, 60, 1_000_000))]).Messages.Should().BeEmpty();
    }

    [Fact]
    public void Calculate_TooManyTracks_ExplainsTheTrackLimit()
    {
        var result = _sut.Calculate([Book("b1", Tracks(101, 60, 1_000_000))]);

        result.Messages.Should().Equal("Track count 101 exceeds the limit of 100.");
    }

    [Fact]
    public void Calculate_TooLong_ExplainsTheDurationLimit()
    {
        var result = _sut.Calculate([Book("b1", Tracks(6, 3600, 1_000_000))]);

        result.Messages.Should().Equal("Total duration 6.0h exceeds the limit of 5.0h.");
    }

    [Fact]
    public void Calculate_TooLarge_ExplainsTheSizeLimit()
    {
        var result = _sut.Calculate([Book("b1", new SourceTrack("c1", 60, 600L * 1024 * 1024))]);

        result.Messages.Should().Equal("Estimated size 600MB exceeds the limit of 500MB.");
    }

    [Fact]
    public void Calculate_ExceedingEveryLimit_ListsTracksThenDurationThenSize()
    {
        // 101 one-hour tracks of 6 MB: 101 tracks, 101 h, 606 MB.
        var result = _sut.Calculate([Book("b1", Tracks(101, 3600, 6L * 1024 * 1024))]);

        result.WithinLimits.Should().BeFalse();
        result.Messages.Should().Equal(
            "Track count 101 exceeds the limit of 100.",
            "Total duration 101.0h exceeds the limit of 5.0h.",
            "Estimated size 606MB exceeds the limit of 500MB.");
    }
}
