using AudioYotoShelf.Core.Configuration;
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
}
