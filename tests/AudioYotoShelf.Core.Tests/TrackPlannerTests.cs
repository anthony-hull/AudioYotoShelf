using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Core.Tests.Helpers;
using FluentAssertions;

namespace AudioYotoShelf.Core.Tests;

public class TrackPlannerTests
{
    private readonly TrackPlanner _sut = new(new YotoCardLimits());

    // =========================================================================
    // Chapters grouping (default)
    // =========================================================================

    [Fact]
    public void Plan_MultiFile_OneTrackPerFile()
    {
        var media = TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 300),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 400),
            TestData.CreateAbsAudioFile(2, "ino-3", duration: 500),
        ]);

        var tracks = _sut.Plan(media);

        tracks.Should().HaveCount(3);
        tracks.Select(t => t.DurationSeconds).Should().Equal(300, 400, 500);
    }

    [Fact]
    public void Plan_SingleFileWithChapters_OneTrackPerChapter_ProrateBytes()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 3600, size: 90_000_000)],
            chapters:
            [
                TestData.CreateAbsChapter(0, "Ch1", 0, 1200),
                TestData.CreateAbsChapter(1, "Ch2", 1200, 2400),
                TestData.CreateAbsChapter(2, "Ch3", 2400, 3600),
            ]);

        var tracks = _sut.Plan(media);

        tracks.Should().HaveCount(3);
        tracks.Should().OnlyContain(t => t.DurationSeconds == 1200);
        tracks.Sum(t => t.EstimatedBytes).Should().BeCloseTo(90_000_000, 10); // proportional split sums back
    }

    [Fact]
    public void Plan_SingleFileNoChapters_OneTrack()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 1800)],
            chapters: []);

        var tracks = _sut.Plan(media);

        tracks.Should().HaveCount(1);
        tracks[0].DurationSeconds.Should().Be(1800);
    }

    [Fact]
    public void Plan_NoAudioFiles_Empty()
    {
        var media = TestData.CreateAbsMedia(audioFiles: [], chapters: []);
        _sut.Plan(media).Should().BeEmpty();
    }

    // =========================================================================
    // SingleTrack grouping
    // =========================================================================

    [Fact]
    public void Plan_SingleTrack_MergesWholeBook()
    {
        var media = TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 600, size: 10_000_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 900, size: 15_000_000),
        ]);

        var tracks = _sut.Plan(media, TrackGrouping.SingleTrack);

        tracks.Should().HaveCount(1);
        tracks[0].DurationSeconds.Should().Be(1500);
        tracks[0].EstimatedBytes.Should().Be(25_000_000);
    }

    // =========================================================================
    // Auto grouping
    // =========================================================================

    [Fact]
    public void Plan_Auto_ShortBook_MergesToOneTrack()
    {
        var media = TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 600, size: 5_000_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 600, size: 5_000_000),
        ]);

        var tracks = _sut.Plan(media, TrackGrouping.Auto);

        tracks.Should().HaveCount(1); // 1200s, 10 MB -> fits one track
    }

    [Fact]
    public void Plan_Auto_LongBook_KeepsChapters()
    {
        var media = TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 2400, size: 5_000_000),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 2400, size: 5_000_000),
        ]);

        var tracks = _sut.Plan(media, TrackGrouping.Auto);

        tracks.Should().HaveCount(2); // 4800s > 1h -> keep per-file
    }

    // =========================================================================
    // Mutation-testing additions — titles, byte proration and the one-track limit
    // =========================================================================

    [Fact]
    public void Plan_MultiFile_TitlesTracksFromChaptersWhenPresent()
    {
        // Audiobookshelf's AudioFile.Index is 1-based (confirmed live against a real book) —
        // file 1 is the first file, and lines up with chapters[0].
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(1), TestData.CreateAbsAudioFile(2)],
            chapters:
            [
                TestData.CreateAbsChapter(0, "Intro", 0, 300),
                TestData.CreateAbsChapter(1, "Outro", 300, 600),
            ]);

        _sut.Plan(media).Select(t => t.Title).Should().Equal("Intro", "Outro");
    }

    [Fact]
    public void Plan_MultiFile_FallsBackToFilenameWhenNoChapterMatchesTheFile()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(1), TestData.CreateAbsAudioFile(2)],
            chapters: [TestData.CreateAbsChapter(0, "Intro", 0, 300)]);

        _sut.Plan(media).Select(t => t.Title).Should().Equal("Intro", "chapter2.mp3");
    }

    [Fact]
    public void Plan_MultiFile_OrdersTracksByFileIndex()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles:
            [
                TestData.CreateAbsAudioFile(1, "ino-2", duration: 400),
                TestData.CreateAbsAudioFile(0, "ino-1", duration: 300),
            ],
            chapters: []);

        _sut.Plan(media).Select(t => t.DurationSeconds).Should().Equal(300, 400);
    }

    [Fact]
    public void Plan_SingleFileNoChapters_TitlesTheTrackWithTheBookTitle()
    {
        var media = TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile()], chapters: []);

        _sut.Plan(media)[0].Title.Should().Be("Test Book");
    }

    [Fact]
    public void Plan_SingleFileNoChapters_UsesUnknownWhenTheBookHasNoTitle()
    {
        var media = WithoutTitle(TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile()], chapters: []));

        _sut.Plan(media)[0].Title.Should().Be("Unknown");
    }

    [Fact]
    public void Plan_SingleTrackGrouping_TitlesTheMergedTrackWithTheBookTitle()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0), TestData.CreateAbsAudioFile(1)], chapters: []);

        _sut.Plan(media, TrackGrouping.SingleTrack)[0].Title.Should().Be("Test Book");
    }

    [Fact]
    public void Plan_SingleTrackGrouping_UsesUnknownWhenTheBookHasNoTitle()
    {
        var media = WithoutTitle(TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0), TestData.CreateAbsAudioFile(1)], chapters: []));

        _sut.Plan(media, TrackGrouping.SingleTrack)[0].Title.Should().Be("Unknown");
    }

    [Fact]
    public void Plan_SingleFileWithChapters_ProratesBytesByTheFilesOwnDuration()
    {
        // The file is 1800 s / 90 MB, the book 3600 s: a 900 s chapter is half the FILE (45 MB), not a quarter.
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 1800, size: 90_000_000)],
            chapters: [TestData.CreateAbsChapter(0, "Ch1", 0, 900)],
            duration: 3600);

        _sut.Plan(media)[0].EstimatedBytes.Should().Be(45_000_000);
    }

    [Fact]
    public void Plan_SingleFileWithChapters_FallsBackToBookDurationWhenFileDurationIsZero()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 0, size: 90_000_000)],
            chapters: [TestData.CreateAbsChapter(0, "Ch1", 0, 1800)],
            duration: 3600);

        _sut.Plan(media)[0].EstimatedBytes.Should().Be(45_000_000);
    }

    [Fact]
    public void Plan_SingleFileWithChapters_GivesEachChapterTheWholeFileWhenNoDurationIsKnown()
    {
        var media = TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", duration: 0, size: 90_000_000)],
            chapters: [TestData.CreateAbsChapter(0, "Ch1", 0, 600), TestData.CreateAbsChapter(1, "Ch2", 600, 1200)],
            duration: 0);

        _sut.Plan(media).Select(t => t.EstimatedBytes).Should().Equal(90_000_000, 90_000_000);
    }

    [Theory]
    [InlineData(1800, 1_000, 1)]            // two files totalling exactly 3600 s: fits one track
    [InlineData(1800.5, 1_000, 2)]          // one second over the duration limit
    [InlineData(600, 52_428_800, 1)]        // two files totalling exactly 100 MB: fits one track
    [InlineData(600, 52_428_801, 2)]        // over the byte limit
    [InlineData(600, 62_914_560, 2)]        // 60 MB each: the SUM (120 MB) is over, though no single file is
    public void Plan_Auto_MergesOnlyWhenTheWholeBookFitsOneTrack(double secondsPerFile, long bytesPerFile, int expectedTracks)
    {
        var media = TestData.CreateAbsMedia(
            audioFiles:
            [
                TestData.CreateAbsAudioFile(0, "ino-1", secondsPerFile, bytesPerFile),
                TestData.CreateAbsAudioFile(1, "ino-2", secondsPerFile, bytesPerFile),
            ],
            chapters: []);

        _sut.Plan(media, TrackGrouping.Auto).Should().HaveCount(expectedTracks);
    }

    private static AbsBookMedia WithoutTitle(AbsBookMedia media) =>
        media with { Metadata = media.Metadata with { Title = null } };
}
