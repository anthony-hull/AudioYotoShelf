using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

// Mutation-testing additions — how a playlist book becomes Yoto tracks: grouping, concatenation, splitting,
// content types, icons, covers and temp-file clean-up. Every test also asserts nothing is left in the temp dir.
public partial class PlaylistTransferOrchestratorTests
{
    private sealed record Upload(string Path, long Length, string ContentType);

    private sealed class CardCapture
    {
        public YotoCardContent? Content { get; set; }
        public YotoCardMetadata? Metadata { get; set; }
        public string? Title { get; set; }
        public string? ExistingCardId { get; set; }
    }

    private static PlaylistItem Item(string id, int position, string title, double[] durations,
        TrackGrouping? grouping = null) => new()
        {
            AbsLibraryItemId = id,
            Position = position,
            BookTitle = title,
            TrackDurations = [.. durations],
            TrackBytes = [.. durations.Select(_ => 1_000_000L)],
            GroupingOverride = grouping,
        };

    private string MakeTempFile(string name, int bytes)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private List<Upload> CaptureUploads()
    {
        var uploads = new List<Upload>();
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Stream stream, long length, string type, IProgress<int>? _, CancellationToken _) =>
            {
                uploads.Add(new Upload(((FileStream)stream).Name, length, type));
                return new YotoTranscodeResult("sha-123", null, null, null);
            });
        return uploads;
    }

    private CardCapture CaptureCard()
    {
        var capture = new CardCapture();
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, YotoCardContent, YotoCardMetadata, string?, string?, CancellationToken>((_, c, m, title, existing, _) =>
            {
                capture.Content = c;
                capture.Metadata = m;
                capture.Title = title;
                capture.ExistingCardId = existing;
            })
            .ReturnsAsync("card-abc");
        return capture;
    }

    private List<string> CaptureDownloadOrder()
    {
        var inodes = new List<string>();
        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string ino, CancellationToken _) =>
            {
                inodes.Add(ino);
                return new MemoryStream(new byte[100]);
            });
        return inodes;
    }

    private void TempDirShouldBeEmpty() =>
        Directory.GetFiles(_tempDir).Should().BeEmpty("every temp file the transfer created must be removed afterwards");

    private static AbsAudioFile Named(int index, string ino, double duration, string filename)
    {
        var file = TestData.CreateAbsAudioFile(index, ino, duration);
        return file with { Metadata = file.Metadata with { Filename = filename } };
    }

    // --- Multi-file book, one track per file

    [Fact]
    public async Task TransferPlaylist_MultiFileBook_UploadsOneTrackPerFileInIndexOrderWithChapterTitles()
    {
        var uploads = CaptureUploads();
        var card = CaptureCard();
        var downloads = CaptureDownloadOrder();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "First Book", [300, 400])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(1, "ino-b", 400), TestData.CreateAbsAudioFile(0, "ino-a", 300)],
            chapters: [TestData.CreateAbsChapter(0, "Intro", 0, 300)]));

        await _sut.TransferPlaylistAsync(playlistId);

        downloads.Should().Equal("ino-a", "ino-b");
        var chapter = card.Content!.Chapters.Single();
        (chapter.Key, chapter.Title).Should().Be(("01", "First Book"));
        chapter.Tracks.Select(t => (t.Key, t.Title, t.TrackUrl, t.Duration, t.FileSize, t.Format, t.Type, t.Channels))
            .Should().Equal(
                ("0101", "Intro", "yoto:#sha-123", 300.0, 100L, "aac", "audio", "stereo"),
                ("0102", "chapter1.mp3", "yoto:#sha-123", 400.0, 100L, "aac", "audio", "stereo"));   // no chapter for file 1: filename
        uploads.Select(u => u.ContentType).Should().Equal("audio/mpeg", "audio/mpeg");
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_ATrackTranscodedToSomethingOtherThanAac_DeclaresWhatItReallyIs()
    {
        // Same defect as the book-transfer path: a declared Format that doesn't match what Yoto
        // actually made fails on the device after a couple of seconds.
        var card = CaptureCard();
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new YotoTranscodeResult("sha-opus", "opus", 300.4, 12_345));
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Solo", [300])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 300)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        var track = card.Content!.Chapters.Single().Tracks.Single();
        (track.Format, track.Duration, track.FileSize).Should().Be(("opus", 300.4, 12_345L));
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_SecondBook_GetsItsOwnChapterKey()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "One", [100]), Item("book-2", 1, "Two", [100])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 100)], chapters: []));
        SetupBook("book-2", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-2", 100)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        card.Content!.Chapters.Select(c => (c.Key, c.Title, c.Tracks.Single().Key))
            .Should().Equal(("01", "One", "0101"), ("02", "Two", "0201"));
        TempDirShouldBeEmpty();
    }

    // --- Single file with chapters

    [Fact]
    public async Task TransferPlaylist_SingleFileWithChapters_DownloadsOnceAndExtractsEachChapterAsATrack()
    {
        var uploads = CaptureUploads();
        var card = CaptureCard();
        var downloads = CaptureDownloadOrder();
        var extracted = new List<(string Input, double Start, double End, string Format)>();
        _chapterExtractor.Setup(c => c.ExtractChapterAsync(
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string input, double start, double end, string format, CancellationToken _) =>
            {
                extracted.Add((input, start, end, format));
                return MakeTempFile($"chapter-{start}.m4a", 50);
            });
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Solo", [100, 250])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 350)],
            chapters: [TestData.CreateAbsChapter(0, "One", 0, 100), TestData.CreateAbsChapter(1, "Two", 100, 350)]));

        await _sut.TransferPlaylistAsync(playlistId);

        downloads.Should().Equal("ino-1");
        extracted.Select(e => (e.Start, e.End, e.Format)).Should().Equal((0.0, 100.0, "m4a"), (100.0, 350.0, "m4a"));
        extracted.Select(e => e.Input).Distinct().Should().ContainSingle().Which.Should().StartWith(_tempDir);
        card.Content!.Chapters.Single().Tracks.Select(t => (t.Title, t.Duration, t.FileSize))
            .Should().Equal(("One", 100.0, 50L), ("Two", 250.0, 50L));
        uploads.Select(u => u.ContentType).Should().Equal("audio/mp4", "audio/mp4");
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_SingleFileWithoutChapters_IsOneTrackTitledAfterTheBook()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Solo Story", [321])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 321)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        card.Content!.Chapters.Single().Tracks.Select(t => (t.Title, t.Duration)).Should().Equal(("Solo Story", 321.0));
        TempDirShouldBeEmpty();
    }

    // --- Merging a whole book into one track

    [Fact]
    public async Task TransferPlaylist_SingleTrackGrouping_ConcatenatesTheFilesInIndexOrderIntoOneTrack()
    {
        var uploads = CaptureUploads();
        var card = CaptureCard();
        var downloads = CaptureDownloadOrder();
        IReadOnlyList<string>? joined = null;
        string? format = null;
        _chapterExtractor.Setup(c => c.ConcatenateAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> parts, string fmt, CancellationToken _) =>
            {
                joined = parts;
                format = fmt;
                return MakeTempFile("merged.m4a", 300);
            });
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Merged Book", [100, 250], TrackGrouping.SingleTrack)]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(1, "ino-b", 250), TestData.CreateAbsAudioFile(0, "ino-a", 100)],
            chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        downloads.Should().Equal("ino-a", "ino-b");
        joined.Should().HaveCount(2).And.OnlyContain(p => p.StartsWith(_tempDir));
        format.Should().Be("m4a");
        var track = card.Content!.Chapters.Single().Tracks.Single();
        (track.Title, track.Duration, track.FileSize).Should().Be(("Merged Book", 350.0, 300L));   // durations add up
        uploads.Single().Should().Be(new Upload(Path.Combine(_tempDir, "merged.m4a"), 300, "audio/mp4"));
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_SingleTrackGroupingWithOneFile_UsesTheDownloadWithoutConcatenating()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "One File", [200], TrackGrouping.SingleTrack)]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 200)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _chapterExtractor.Verify(c => c.ConcatenateAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        card.Content!.Chapters.Single().Tracks.Should().ContainSingle();
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_ChaptersGrouping_NeverConcatenates()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Two Files", [100, 100])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-a", 100), TestData.CreateAbsAudioFile(1, "ino-b", 100)],
            chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _chapterExtractor.Verify(c => c.ConcatenateAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        card.Content!.Chapters.Single().Tracks.Should().HaveCount(2);
        TempDirShouldBeEmpty();
    }

    [Theory]
    [InlineData(600, true)]     // two 10-minute files fit one Yoto track: merged
    [InlineData(2400, false)]   // 80 minutes in total exceeds the one-hour track limit: kept per file
    public async Task TransferPlaylist_AutoGrouping_MergesOnlyABookThatFitsOneTrack(double secondsPerFile, bool shouldMerge)
    {
        var card = CaptureCard();
        _chapterExtractor.Setup(c => c.ConcatenateAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => MakeTempFile("merged.m4a", 300));
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Auto,
            items: [Item("book-1", 0, "Auto Book", [secondsPerFile, secondsPerFile])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            audioFiles:
            [
                TestData.CreateAbsAudioFile(0, "ino-a", secondsPerFile),
                TestData.CreateAbsAudioFile(1, "ino-b", secondsPerFile),
            ],
            chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        card.Content!.Chapters.Single().Tracks.Should().HaveCount(shouldMerge ? 1 : 2);
        _chapterExtractor.Verify(c => c.ConcatenateAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            shouldMerge ? Times.Once() : Times.Never());
        TempDirShouldBeEmpty();
    }

    // --- Splitting an oversized track

    [Fact]
    public async Task TransferPlaylist_TrackOverTheOneHourLimit_IsSplitIntoNumberedParts()
    {
        var card = CaptureCard();
        double? requestedSegmentSeconds = null;
        _chapterExtractor.Setup(c => c.SplitAsync(
                It.IsAny<string>(), It.IsAny<double>(), "m4a", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, double seconds, string _, CancellationToken _) =>
            {
                requestedSegmentSeconds = seconds;
                return (IReadOnlyList<string>)[MakeTempFile("part1.m4a", 60), MakeTempFile("part2.m4a", 60)];
            });
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Long Book", [7200])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 7200)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        requestedSegmentSeconds.Should().Be(3600);   // 7200 s over 2 parts
        card.Content!.Chapters.Single().Tracks.Select(t => (t.Key, t.Title, t.Duration, t.FileSize)).Should().Equal(
            ("0101", "Long Book (Part 1)", 3600.0, 60L),
            ("0102", "Long Book (Part 2)", 3600.0, 60L));
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_TrackWithinTheLimit_IsNotSplit()
    {
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Short", [3600])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 3600)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _chapterExtractor.Verify(c => c.SplitAsync(
            It.IsAny<string>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        TempDirShouldBeEmpty();
    }

    // --- Content type and file extension

    [Theory]
    [InlineData("book.m4a", "audio/mp4", ".m4a")]
    [InlineData("book.M4B", "audio/mp4", ".M4B")]
    [InlineData("book.mp4", "audio/mp4", ".mp4")]
    [InlineData("book.aac", "audio/mp4", ".aac")]
    [InlineData("book.mp3", "audio/mpeg", ".mp3")]
    [InlineData("book.ogg", "audio/ogg", ".ogg")]
    [InlineData("book.opus", "audio/ogg", ".opus")]
    [InlineData("book.flac", "audio/flac", ".flac")]
    [InlineData("book.wav", "audio/wav", ".wav")]
    [InlineData("book", "audio/mpeg", ".mp3")]         // no extension: defaults to .mp3
    public async Task TransferPlaylist_ChoosesTheContentTypeFromTheFileExtension(string filename, string contentType, string extension)
    {
        var uploads = CaptureUploads();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Typed", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [Named(0, "ino-1", 60, filename)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        var upload = uploads.Single();
        upload.ContentType.Should().Be(contentType);
        Path.GetExtension(upload.Path).Should().Be(extension);
        TempDirShouldBeEmpty();
    }

    // --- Book icon

    [Fact]
    public async Task TransferPlaylist_GeneratesOneIconPerBookAndReferencesItOnTheChapterAndItsTracks()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "A Very Long Playlist Book Title", [100, 100])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            TestData.CreateAbsMetadata(genres: ["Fantasy", "Adventure"]),
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-a", 100), TestData.CreateAbsAudioFile(1, "ino-b", 100)],
            chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _iconService.Verify(s => s.GenerateChapterIconAsync(
            "A Very Long Playlist Book Title", "A Very Long Playlist Book Title", "Fantasy", It.IsAny<CancellationToken>()), Times.Once);
        _yotoService.Verify(s => s.UploadCustomIconAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(), "A Very Long Playlist.png", It.IsAny<CancellationToken>()), Times.Once);
        var chapter = card.Content!.Chapters.Single();
        var expectedIcon = $"yoto:#{TestData.CreateYotoIconUpload().MediaId}";
        chapter.Display!.Icon16X16.Should().Be(expectedIcon);
        chapter.Tracks.Select(t => t.Display!.Icon16X16).Should().Equal(expectedIcon, expectedIcon);
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_ShortTitle_IsUsedWholeForTheIconFileName()
    {
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Tiny", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _yotoService.Verify(s => s.UploadCustomIconAsync(
            It.IsAny<string>(), It.IsAny<byte[]>(), "Tiny.png", It.IsAny<CancellationToken>()), Times.Once);
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_BookWithoutGenres_AsksForAnIconWithNoGenre()
    {
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Plain", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(
            TestData.CreateAbsMetadata() with { Genres = [] },
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _iconService.Verify(s => s.GenerateChapterIconAsync(
            "Plain", "Plain", null, It.IsAny<CancellationToken>()), Times.Once);
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_WhenIconGenerationFails_StillBuildsTheCardWithoutIcons()
    {
        var card = CaptureCard();
        _iconService.Setup(s => s.GenerateChapterIconAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("quota"));
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "No Icons", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        var chapter = card.Content!.Chapters.Single();
        (chapter.Display, chapter.Tracks.Single().Display).Should().Be((null, null));
        TempDirShouldBeEmpty();
    }

    // --- Temp directory and connections

    [Fact]
    public async Task TransferPlaylist_CreatesTheTempDirectoryWhenItDoesNotExistYet()
    {
        var nested = Path.Combine(_tempDir, "not-yet-created");
        var sut = CreateSut(nested);
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Fresh", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await sut.TransferPlaylistAsync(playlistId);

        card.Content.Should().NotBeNull();
        Directory.GetFiles(nested).Should().BeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_UserWithoutAnAudiobookshelfConnection_Throws()
    {
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Any", [60])]);
        var user = await _fixture.DbContext.UserConnections.SingleAsync();
        user.AudiobookshelfToken = null;
        await _fixture.DbContext.SaveChangesAsync();

        var act = () => _sut.TransferPlaylistAsync(playlistId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No valid Audiobookshelf connection");
    }

    [Fact]
    public async Task TransferPlaylist_UserWithoutAYotoConnection_Throws()
    {
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Any", [60])]);
        var user = await _fixture.DbContext.UserConnections.SingleAsync();
        user.YotoAccessToken = null;
        await _fixture.DbContext.SaveChangesAsync();

        var act = () => _sut.TransferPlaylistAsync(playlistId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No valid Yoto connection");
    }

    // --- Cover art

    [Fact]
    public async Task TransferPlaylist_SingleBook_UsesItsCoverForTheCard()
    {
        var card = CaptureCard();
        _absService.Setup(s => s.GetCoverImageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2]));
        _yotoService.Setup(s => s.UploadCoverImageAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://covers.yotoplay.com/book.jpg");
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Cover Book", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _absService.Verify(s => s.GetCoverImageAsync(
            It.IsAny<string>(), It.IsAny<string>(), "book-1", It.IsAny<CancellationToken>()), Times.Once);
        card.Metadata!.Cover.Should().Be(new YotoCover("https://covers.yotoplay.com/book.jpg"));
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_SeveralBooks_UploadsNoCover()
    {
        var card = CaptureCard();
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "One", [60]), Item("book-2", 1, "Two", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));
        SetupBook("book-2", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-2", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        _absService.Verify(s => s.GetCoverImageAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        card.Metadata!.Cover.Should().BeNull();
        TempDirShouldBeEmpty();
    }

    [Fact]
    public async Task TransferPlaylist_WhenTheCoverUploadFails_StillBuildsTheCardWithoutACover()
    {
        var card = CaptureCard();
        _yotoService.Setup(s => s.UploadCoverImageAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rejected"));
        _absService.Setup(s => s.GetCoverImageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1]));
        var playlistId = await SeedPlaylistAsync(grouping: TrackGrouping.Chapters,
            items: [Item("book-1", 0, "Cover Book", [60])]);
        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1", 60)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        card.Metadata!.Cover.Should().BeNull();
        card.Content.Should().NotBeNull();
        TempDirShouldBeEmpty();
    }
}
