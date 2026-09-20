using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

// Mutation-testing additions — the pipeline steps: track mappings, uploads and their progress arithmetic,
// icon generation, and the card content sent to Yoto.
public partial class TransferOrchestratorTests
{
    private static AbsAudioFile[] Files(params (int Index, string Ino, double Duration, long Size)[] files) =>
        files.Select(f => TestData.CreateAbsAudioFile(f.Index, f.Ino, f.Duration, f.Size)).ToArray();

    private async Task<(UserConnection User, CardTransfer Transfer)> SeedTransferAsync()
    {
        var user = await SeedUserAsync();
        var transfer = TestData.CreateCardTransfer(user.Id);
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();
        return (user, transfer);
    }

    private async Task<List<TrackMapping>> StoredMappingsAsync(Guid transferId)
    {
        await using var fresh = _dbFixture.NewContext();
        return await fresh.TrackMappings.Where(m => m.CardTransferId == transferId).OrderBy(m => m.ChapterIndex).ToListAsync();
    }

    // --- BuildTrackMappingsAsync

    [Fact]
    public async Task BuildTrackMappings_ItemWithNoAudioFiles_Throws()
    {
        var (user, transfer) = await SeedTransferAsync();
        var item = TestData.CreateAbsLibraryItem(media: TestData.CreateAbsMedia(audioFiles: [], chapters: []));

        var act = () => _sut.BuildTrackMappingsAsync(user, item, transfer, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Item has no audio files to transfer");
    }

    [Fact]
    public async Task BuildTrackMappings_MultiFileBook_MapsEachFileInIndexOrderAndSavesThem()
    {
        var (user, transfer) = await SeedTransferAsync();
        var media = TestData.CreateAbsMedia(
            audioFiles: Files((1, "ino-b", 200, 2_000), (0, "ino-a", 100, 1_000)),
            chapters: [TestData.CreateAbsChapter(0, "Intro", 0, 100)]);

        var (mappings, chapterPaths) = await _sut.BuildTrackMappingsAsync(
            user, TestData.CreateAbsLibraryItem(media: media), transfer, CancellationToken.None);

        chapterPaths.Should().BeEmpty();
        var stored = await StoredMappingsAsync(transfer.Id);
        stored.Select(m => (m.AbsFileIno, m.ChapterTitle, m.ChapterIndex, m.StartTime, m.EndTime, m.FileSizeBytes))
            .Should().Equal(
                ("ino-a", "Intro", 0, 0.0, 100.0, 1_000L),
                ("ino-b", "chapter1.mp3", 1, 0.0, 200.0, 2_000L));   // no chapter for file 1: falls back to its filename
        mappings.Select(m => m.AbsFileIno).Should().Equal("ino-a", "ino-b");
    }

    [Fact]
    public async Task BuildTrackMappings_SingleFileWithoutChapters_IsOneTrackTitledAfterTheBook()
    {
        var (user, transfer) = await SeedTransferAsync();
        var media = TestData.CreateAbsMedia(
            audioFiles: Files((0, "ino-1", 321, 4_321)), chapters: []);

        var (returned, _) = await _sut.BuildTrackMappingsAsync(
            user, TestData.CreateAbsLibraryItem(media: media), transfer, CancellationToken.None);

        returned.Select(m => m.AbsFileIno).Should().Equal("ino-1");
        var only = (await StoredMappingsAsync(transfer.Id)).Single();
        (only.AbsFileIno, only.ChapterTitle, only.ChapterIndex, only.StartTime, only.EndTime, only.FileSizeBytes)
            .Should().Be(("ino-1", "Test Book", 0, 0.0, 321.0, 4_321L));
    }

    [Fact]
    public async Task BuildTrackMappings_SingleFileWithoutChaptersOrTitle_IsCalledTrack1()
    {
        var (user, transfer) = await SeedTransferAsync();
        var media = TestData.CreateAbsMedia(TestData.CreateAbsMetadata() with { Title = null },
            audioFiles: Files((0, "ino-1", 60, 1_000)), chapters: []);

        await _sut.BuildTrackMappingsAsync(user, TestData.CreateAbsLibraryItem(media: media), transfer, CancellationToken.None);

        (await StoredMappingsAsync(transfer.Id)).Single().ChapterTitle.Should().Be("Track 1");
    }

    [Fact]
    public async Task BuildTrackMappings_SingleFileWithChapters_DownloadsOnceAndExtractsEachChapter()
    {
        var tempDir = Directory.CreateTempSubdirectory("ays-build-").FullName;
        try
        {
            var sut = CreateSut(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = Path.Combine(tempDir, "not-yet-created") })
                .Build());
            var (user, transfer) = await SeedTransferAsync();
            var media = TestData.CreateAbsMedia(
                audioFiles: Files((0, "ino-1", 600, 9_000)),
                chapters: [TestData.CreateAbsChapter(0, "One", 0, 250), TestData.CreateAbsChapter(1, "Two", 250, 600)]);
            _absService.Setup(s => s.DownloadAudioFileAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
            var seenInputs = new List<(string Path, string Hex, double Start, double End)>();
            _chapterExtractor.Setup(c => c.ExtractChapterAsync(
                    It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), "m4a", It.IsAny<CancellationToken>()))
                .Returns((string input, double start, double end, string _, CancellationToken _) =>
                {
                    seenInputs.Add((input, Convert.ToHexString(File.ReadAllBytes(input)), start, end));
                    return Task.FromResult(_tempChapterFile);
                });

            var (mappings, chapterPaths) = await sut.BuildTrackMappingsAsync(
                TestData.CreateUserConnection(), TestData.CreateAbsLibraryItem("book-9", media), transfer, CancellationToken.None);

            _absService.Verify(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), "book-9", "ino-1", It.IsAny<CancellationToken>()), Times.Once);
            var expectedInput = Path.Combine(tempDir, "not-yet-created", $"{transfer.Id}_input.mp3");
            seenInputs.Should().Equal(
                (expectedInput, "01020304", 0.0, 250.0),
                (expectedInput, "01020304", 250.0, 600.0));
            chapterPaths.Should().Equal(new Dictionary<int, string> { [0] = _tempChapterFile, [1] = _tempChapterFile });
            (await StoredMappingsAsync(transfer.Id))
                .Select(m => (m.AbsFileIno, m.ChapterTitle, m.ChapterIndex, m.StartTime, m.EndTime, m.FileSizeBytes))
                .Should().Equal(
                    ("ino-1:ch0", "One", 0, 0.0, 250.0, 100L),
                    ("ino-1:ch1", "Two", 1, 250.0, 600.0, 100L));
            mappings.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // --- UploadTracksAsync

    private static TrackMapping Mapping(Guid transferId, string ino, int index) =>
        TestData.CreateTrackMapping(transferId, ino, $"Chapter {index + 1}", index);

    /// <summary>Runs progress callbacks inline so a test can read their effect straight after each report.</summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                d(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    [Fact]
    public async Task UploadTracks_ReportsOverallProgressAndTheUploadOrTranscodeStep()
    {
        var (user, transfer) = await SeedTransferAsync();
        var mappings = new List<TrackMapping> { Mapping(transfer.Id, "ino-a", 0), Mapping(transfer.Id, "ino-b", 1) };
        _db.TrackMappings.AddRange(mappings);
        await _db.SaveChangesAsync();
        var updates = CaptureNotifications();
        var seenProgress = new List<int>();
        var reports = new Queue<int>([30, 60, 50, 100]);   // track 1 at 30% then 60%; track 2 at 50% then 100%
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Stream _, long _, string _, IProgress<int>? progress, CancellationToken _) =>
            {
                for (var n = 0; n < 2; n++)
                {
                    progress!.Report(reports.Dequeue());
                    seenProgress.Add(transfer.ProgressPercent);
                }
                return "sha-x";
            });
        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            await _sut.UploadTracksAsync("token", mappings, new Dictionary<int, string>(), transfer, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        // 20 + trunc((i + p/100) / 2 * 50): track 1 -> 27 (p=30), 35 (p=60); track 2 -> 57 (p=50), 70 (p=100).
        seenProgress.Should().Equal(27, 35, 57, 70);
        updates.Select(u => u.CurrentStep).Should().Equal(
            "Downloading track 1/2 from Audiobookshelf…", "Uploading track 1/2…", "Transcoding track 1/2 on Yoto…",
            "Track 1/2 is on Yoto",
            "Downloading track 2/2 from Audiobookshelf…", "Uploading track 2/2…", "Transcoding track 2/2 on Yoto… 100%",
            "Track 2/2 is on Yoto");
    }

    [Fact]
    public async Task UploadTracks_NeverReportsMoreThan70PercentWhileUploading()
    {
        var (user, transfer) = await SeedTransferAsync();
        var mappings = new List<TrackMapping> { Mapping(transfer.Id, "ino-a", 0) };
        _db.TrackMappings.AddRange(mappings);
        await _db.SaveChangesAsync();
        var seen = -1;
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Stream _, long _, string _, IProgress<int>? progress, CancellationToken _) =>
            {
                progress!.Report(250);   // a runaway value: the cap must hold
                seen = transfer.ProgressPercent;
                return "sha-x";
            });
        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            await _sut.UploadTracksAsync("token", mappings, new Dictionary<int, string>(), transfer, CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        seen.Should().Be(70);
    }

    [Fact]
    public async Task UploadTracks_ExtractedChapter_IsUploadedFromItsFileAsMp4AndTheStreamIsClosed()
    {
        var (user, transfer) = await SeedTransferAsync();
        var mapping = Mapping(transfer.Id, "ino-1:ch0", 0);
        _db.TrackMappings.Add(mapping);
        await _db.SaveChangesAsync();
        (long Length, string Type, Stream Stream)? sent = null;
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Stream stream, long length, string type, IProgress<int>? _, CancellationToken _) =>
            {
                sent = (length, type, stream);
                return "sha-chapter";
            });

        await _sut.UploadTracksAsync("token", [mapping], new Dictionary<int, string> { [0] = _tempChapterFile }, transfer, CancellationToken.None);

        (sent!.Value.Length, sent.Value.Type).Should().Be((100L, "audio/mp4"));
        sent.Value.Stream.CanRead.Should().BeFalse("the stream is disposed once the upload returns");
        _absService.Verify(s => s.DownloadAudioFileAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadTracks_WholeFileTrack_IsDownloadedFirstAndUploadedAsMpeg_SavingTheShaAndUrl()
    {
        var (user, transfer) = await SeedTransferAsync();
        var mapping = Mapping(transfer.Id, "ino-9", 0);
        _db.TrackMappings.Add(mapping);
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[250]));
        (long Length, string Type)? sent = null;
        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Stream _, long length, string type, IProgress<int>? _, CancellationToken _) =>
            {
                sent = (length, type);
                return "sha-whole";
            });

        try
        {
            await _sut.UploadTracksAsync("token", [mapping], new Dictionary<int, string>(), transfer, CancellationToken.None);
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetTempPath(), $"{transfer.Id}_track0.tmp"));
        }

        sent.Should().Be((250L, "audio/mpeg"));
        _absService.Verify(s => s.DownloadAudioFileAsync(
            user.AudiobookshelfUrl, user.AudiobookshelfToken!, transfer.AbsLibraryItemId, "ino-9", It.IsAny<CancellationToken>()), Times.Once);
        var stored = (await StoredMappingsAsync(transfer.Id)).Single();
        (stored.YotoTranscodedSha256, stored.YotoTrackUrl).Should().Be(("sha-whole", "yoto:#sha-whole"));
    }

    // --- GenerateIconsAsync

    [Fact]
    public async Task GenerateIcons_BuildsThePromptFromTheFirstGenreAndTitleAndSavesTheIcon()
    {
        var (user, transfer) = await SeedTransferAsync();
        var metadata = TestData.CreateAbsMetadata("A Long Book Title Indeed", genres: ["Fantasy", "Adventure"]);
        var media = TestData.CreateAbsMedia(metadata);
        var mapping = TestData.CreateTrackMapping(transfer.Id, "ino-1", "An Extremely Long Chapter Name", 0);
        _db.TrackMappings.Add(mapping);
        await _db.SaveChangesAsync();

        var icons = await _sut.GenerateIconsAsync("token", user, media, [mapping], CancellationToken.None);

        _iconService.Verify(s => s.BuildChapterIconPrompt("An Extremely Long Chapter Name", "A Long Book Title Indeed", "Fantasy"), Times.Once);
        _iconService.Verify(s => s.GenerateChapterIconAsync(
            "An Extremely Long Chapter Name", "A Long Book Title Indeed", "Fantasy", It.IsAny<CancellationToken>()), Times.Once);
        _yotoService.Verify(s => s.UploadCustomIconAsync(
            "token", It.IsAny<byte[]>(), "ch0_An Extremely Long Ch.png", It.IsAny<CancellationToken>()), Times.Once);
        await using var fresh = _dbFixture.NewContext();
        var saved = await fresh.GeneratedIcons.SingleAsync();
        (saved.UserConnectionId, saved.ContextTitle, saved.Prompt, saved.Source, saved.TimesUsed)
            .Should().Be((user.Id, "A Long Book Title Indeed - An Extremely Long Chapter Name",
                "prompt: An Extremely Long Chapter Name", IconSource.GeminiGenerated, 1));
        icons.Should().Equal(new Dictionary<int, string> { [0] = $"yoto:#{saved.YotoMediaId}" });
    }

    [Fact]
    public async Task GenerateIcons_ShortChapterTitle_IsUsedWholeInTheFileName()
    {
        var (user, transfer) = await SeedTransferAsync();
        var mapping = TestData.CreateTrackMapping(transfer.Id, "ino-1", "Short", 2);
        _db.TrackMappings.Add(mapping);
        await _db.SaveChangesAsync();

        await _sut.GenerateIconsAsync("token", user, TestData.CreateAbsMedia(), [mapping], CancellationToken.None);

        _yotoService.Verify(s => s.UploadCustomIconAsync(
            "token", It.IsAny<byte[]>(), "ch0_Short.png", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateIcons_BookWithoutGenresOrTitle_UsesNoGenreAndUnknown()
    {
        var (user, transfer) = await SeedTransferAsync();
        var media = TestData.CreateAbsMedia(TestData.CreateAbsMetadata() with { Title = null, Genres = [] });
        var mapping = TestData.CreateTrackMapping(transfer.Id, "ino-1", "Chapter", 0);
        _db.TrackMappings.Add(mapping);
        await _db.SaveChangesAsync();

        await _sut.GenerateIconsAsync("token", user, media, [mapping], CancellationToken.None);

        _iconService.Verify(s => s.BuildChapterIconPrompt("Chapter", "Unknown", null), Times.Once);
    }

    // --- CreateYotoCardAsync

    private async Task<(YotoCardContent Content, YotoCardMetadata Metadata, string? Title, string? ExistingCardId)> BuildCardAsync(
        CardTransfer transfer, AbsBookMetadata metadata, List<TrackMapping> mappings,
        Dictionary<int, string>? icons = null, string? coverUrl = null)
    {
        YotoCardContent? content = null;
        YotoCardMetadata? sentMetadata = null;
        string? title = null;
        string? existing = null;
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, YotoCardContent, YotoCardMetadata, string?, string?, CancellationToken>((_, c, m, t, e, _) =>
            {
                content = c;
                sentMetadata = m;
                title = t;
                existing = e;
            })
            .ReturnsAsync("card-x");

        await _sut.CreateYotoCardAsync("token", transfer, metadata, mappings, icons ?? [], coverUrl, CancellationToken.None);

        return (content!, sentMetadata!, title, existing);
    }

    private static TrackMapping CardMapping(int index, string? url = "yoto:#sha") => new()
    {
        AbsFileIno = $"ino-{index}",
        ChapterTitle = $"Chapter {index + 1}",
        ChapterIndex = index,
        StartTime = 10,
        EndTime = 70,
        FileSizeBytes = 5_000,
        YotoTrackUrl = url,
    };

    [Fact]
    public async Task CreateYotoCard_BuildsOneChapterPerTrackWithNumberedKeysAndTheStoredTrackDetails()
    {
        var transfer = TestData.CreateCardTransfer();
        var withTranscodeInfo = CardMapping(1);
        withTranscodeInfo.TranscodedDuration = 55.5;
        withTranscodeInfo.TranscodedFileSize = 4_444;

        var (content, _, _, _) = await BuildCardAsync(
            transfer, TestData.CreateAbsMetadata(), [CardMapping(0, url: null), withTranscodeInfo],
            icons: new Dictionary<int, string> { [1] = "yoto:#icon-1" });

        content.Chapters.Select(c => (c.Key, c.Title, c.Display?.Icon16X16)).Should().Equal(
            ("01", "Chapter 1", null), ("02", "Chapter 2", "yoto:#icon-1"));
        var first = content.Chapters[0].Tracks.Single();
        (first.Key, first.Title, first.TrackUrl, first.Format, first.Type, first.Channels, first.Display)
            .Should().Be(("0101", "Chapter 1", "", "aac", "audio", "stereo", null));
        (first.Duration, first.FileSize).Should().Be((60.0, 5_000L));   // no transcoded values: end - start, source size
        var second = content.Chapters[1].Tracks.Single();
        (second.Key, second.TrackUrl, second.Duration, second.FileSize, second.Display!.Icon16X16)
            .Should().Be(("0201", "yoto:#sha", 55.5, 4_444L, "yoto:#icon-1"));
    }

    [Fact]
    public async Task CreateYotoCard_SetsTheStandardPlaybackConfig()
    {
        var (content, _, _, _) = await BuildCardAsync(
            TestData.CreateCardTransfer(), TestData.CreateAbsMetadata(), [CardMapping(0)]);

        (content.Config, content.PlaybackType, content.Version)
            .Should().Be((new YotoCardConfig(true, true, true), "linear", "1"));
    }

    [Fact]
    public async Task CreateYotoCard_InteractiveTransfer_UsesInteractivePlayback()
    {
        var transfer = TestData.CreateCardTransfer();
        transfer.PlaybackType = PlaybackType.Interactive;

        var (content, _, _, _) = await BuildCardAsync(transfer, TestData.CreateAbsMetadata(), [CardMapping(0)]);

        content.PlaybackType.Should().Be("interactive");
    }

    [Fact]
    public async Task CreateYotoCard_UsesTheItemMetadataForTheCardDetails()
    {
        var transfer = TestData.CreateCardTransfer();
        transfer.Category = YotoCategory.Music;
        transfer.OverrideMinAge = 4;   // override wins; max keeps the suggestion (10)
        var metadata = TestData.CreateAbsMetadata("Real Title", genres: ["Fantasy"], description: "About it") with
        {
            Language = "de",
            Narrators = ["First Reader", "Second Reader"],
        };

        var (_, sent, title, _) = await BuildCardAsync(transfer, metadata, [CardMapping(0)], coverUrl: "https://cover");

        title.Should().Be("Real Title");
        (sent.Author, sent.Category, sent.Description, sent.MinAge, sent.MaxAge, sent.ReadBy)
            .Should().Be(("Test Author", "music", "About it", 4, 10, "First Reader"));
        sent.Genre.Should().Equal("Fantasy");
        sent.Languages.Should().Equal("de");
        sent.Cover.Should().Be(new YotoCover("https://cover"));
    }

    [Fact]
    public async Task CreateYotoCard_ItemWithoutTitleAuthorNarratorLanguageOrCover_FallsBackOrOmits()
    {
        var transfer = TestData.CreateCardTransfer(title: "Stored Title");
        var metadata = TestData.CreateAbsMetadata() with
        {
            Title = null,
            Authors = [],
            Narrators = [],
            Description = null,
            Subtitle = null,
            Language = null,
        };

        var (_, sent, title, _) = await BuildCardAsync(transfer, metadata, [CardMapping(0)]);

        title.Should().Be("Stored Title");
        (sent.Author, sent.ReadBy, sent.Languages, sent.Cover).Should().Be((null, null, null, null));
        sent.Description.Should().Be("Stored Title");   // description -> subtitle -> title
    }

    [Fact]
    public async Task CreateYotoCard_DescriptionFallsBackToTheSubtitleBeforeTheTitle()
    {
        var metadata = TestData.CreateAbsMetadata() with { Description = null, Subtitle = "The Subtitle" };

        var (_, sent, _, _) = await BuildCardAsync(TestData.CreateCardTransfer(), metadata, [CardMapping(0)]);

        sent.Description.Should().Be("The Subtitle");
    }

    [Fact]
    public async Task CreateYotoCard_TransferAlreadyHoldingACard_UpdatesThatCard()
    {
        var transfer = TestData.CreateCardTransfer();
        transfer.YotoCardId = "own-card";
        var earlier = TestData.CreateCardTransfer(transfer.UserConnectionId);
        earlier.YotoCardId = "earlier-card";
        _db.CardTransfers.AddRange(transfer, earlier);
        await _db.SaveChangesAsync();

        var (_, _, _, existing) = await BuildCardAsync(transfer, TestData.CreateAbsMetadata(), [CardMapping(0)]);

        existing.Should().Be("own-card");
    }

    [Fact]
    public async Task CreateYotoCard_NewTransferOfAnItemAlreadyOnACard_UpdatesTheNewestOtherCardForThatItem()
    {
        var owner = await SeedUserAsync();
        CardTransfer Make(string item, string? card, int daysAgo)
        {
            var t = TestData.CreateCardTransfer(owner.Id, item);
            t.YotoCardId = card;
            t.CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
            return t;
        }
        var current = Make("item-123", null, 0);
        _db.CardTransfers.AddRange(
            current,
            Make("item-123", "oldest-card", 9),
            Make("item-123", "newest-card", 3),
            Make("item-123", null, 1),          // newer, but never produced a card
            Make("other-item", "other-card", 0));
        await _db.SaveChangesAsync();

        var (_, _, _, existing) = await BuildCardAsync(current, TestData.CreateAbsMetadata(), [CardMapping(0)]);

        existing.Should().Be("newest-card");
    }

    [Fact]
    public async Task CreateYotoCard_ItemNeverOnACard_CreatesANewOne()
    {
        var (_, transfer) = await SeedTransferAsync();

        var (_, _, _, existing) = await BuildCardAsync(transfer, TestData.CreateAbsMetadata(), [CardMapping(0)]);

        existing.Should().BeNull();
    }

    // --- Yoto language codes

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("en", "en")]
    [InlineData(" EN-GB ", "en-gb")]
    [InlineData("en-us", "en-us")]
    [InlineData("fr", "fr")]
    [InlineData("FR-FR", "fr-fr")]
    [InlineData("es", "es")]
    [InlineData("es-es", "es-es")]
    [InlineData("es-419", "es-419")]
    [InlineData("de", "de")]
    [InlineData("it", "it")]
    [InlineData("zh", "zh_Hans")]
    [InlineData("zh-hans", "zh_Hans")]
    [InlineData("ZH_HANS", "zh_Hans")]
    [InlineData("eng", "en")]                  // starts with "en"
    [InlineData("middle english", "en")]       // contains "english" only
    [InlineData("fra", "fr")]
    [InlineData("canadian french", "fr")]
    [InlineData("esp", "es")]
    [InlineData("spa", "es")]
    [InlineData("castilian spanish", "es")]
    [InlineData("deutsch", "de")]
    [InlineData("ger", "de")]
    [InlineData("old german", "de")]
    [InlineData("ita", "it")]
    [InlineData("modern italian", "it")]
    [InlineData("zho", "zh_Hans")]
    [InlineData("mandarin chinese", "zh_Hans")]
    [InlineData("klingon", null)]
    [InlineData("jpn", null)]
    public async Task CreateYotoCard_MapsTheBookLanguageToAYotoAcceptedCode(string? language, string? expected)
    {
        var metadata = TestData.CreateAbsMetadata() with { Language = language };

        var (_, sent, _, _) = await BuildCardAsync(TestData.CreateCardTransfer(), metadata, [CardMapping(0)]);

        if (expected is null)
            sent.Languages.Should().BeNull();
        else
            sent.Languages.Should().Equal(expected);
    }
}
