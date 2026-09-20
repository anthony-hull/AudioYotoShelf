using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public partial class PlaylistTransferOrchestratorTests : IDisposable
{
    private readonly InMemoryDbFixture _fixture;
    private readonly Mock<IAudiobookshelfService> _absService = new();
    private readonly Mock<IYotoService> _yotoService = new();
    private readonly Mock<IIconGenerationService> _iconService = new();
    private readonly Mock<IChapterExtractor> _chapterExtractor = new();
    private readonly PlaylistTransferOrchestrator _sut;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("ays-playlist-").FullName;

    public PlaylistTransferOrchestratorTests()
    {
        _fixture = new InMemoryDbFixture();
        _sut = CreateSut(_tempDir);

        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-123");
        _yotoService.Setup(s => s.UploadCustomIconAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateYotoIconUpload());
        _iconService.Setup(s => s.GenerateChapterIconAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[100]));
    }

    private PlaylistTransferOrchestrator CreateSut(string tempDirectory) => new(
        _fixture.DbContext, _absService.Object, _yotoService.Object,
        _iconService.Object, _chapterExtractor.Object,
        new TrackPlanner(new YotoCardLimits()), new CardCapacityCalculator(new YotoCardLimits()),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = tempDirectory })
            .Build(),
        Mock.Of<ILogger<PlaylistTransferOrchestrator>>());

    public void Dispose()
    {
        _fixture.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<Guid> SeedPlaylistAsync(PlaylistStatus status = PlaylistStatus.Draft,
        TrackGrouping grouping = TrackGrouping.Chapters, params PlaylistItem[] items)
    {
        var user = TestData.CreateUserConnection();
        _fixture.DbContext.UserConnections.Add(user);

        var playlist = new Playlist
        {
            UserConnectionId = user.Id,
            Name = "Mix",
            DefaultGrouping = grouping,
            Status = status
        };
        foreach (var item in items)
        {
            item.PlaylistId = playlist.Id;
            playlist.Items.Add(item);
        }
        _fixture.DbContext.Playlists.Add(playlist);
        await _fixture.DbContext.SaveChangesAsync();
        return playlist.Id;
    }

    private void SetupBook(string itemId, AudioYotoShelf.Core.DTOs.Audiobookshelf.AbsBookMedia media) =>
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem(itemId, media));

    [Fact]
    public async Task TransferPlaylist_BuildsOneCardWithChapterPerBook()
    {
        YotoCardContent? captured = null;
        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, YotoCardContent, YotoCardMetadata, string?, string?, CancellationToken>(
                (_, content, _, _, _, _) => captured = content)
            .ReturnsAsync("card-abc");

        var playlistId = await SeedPlaylistAsync(
            grouping: TrackGrouping.Chapters,
            items:
            [
                new PlaylistItem { AbsLibraryItemId = "book-1", Position = 0, BookTitle = "Book 1",
                    TrackDurations = [300, 300], TrackBytes = [1_000_000, 1_000_000] },
                new PlaylistItem { AbsLibraryItemId = "book-2", Position = 1, BookTitle = "Book 2",
                    TrackDurations = [300], TrackBytes = [1_000_000] },
            ]);

        SetupBook("book-1", TestData.CreateAbsMedia(audioFiles:
        [
            TestData.CreateAbsAudioFile(0, "ino-1", duration: 300),
            TestData.CreateAbsAudioFile(1, "ino-2", duration: 300),
        ]));
        SetupBook("book-2", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-3", duration: 300)], chapters: []));

        await _sut.TransferPlaylistAsync(playlistId);

        var playlist = await _fixture.DbContext.Playlists.FirstAsync(p => p.Id == playlistId);
        playlist.Status.Should().Be(PlaylistStatus.Transferred);
        playlist.YotoCardId.Should().Be("card-abc");

        captured.Should().NotBeNull();
        captured!.Chapters.Should().HaveCount(2);                 // one chapter per book
        captured.Chapters.Sum(c => c.Tracks.Length).Should().Be(3); // 2 + 1 tracks
    }

    [Fact]
    public async Task TransferPlaylist_OverCapacity_FailsWithoutBuildingCard()
    {
        var playlistId = await SeedPlaylistAsync(
            grouping: TrackGrouping.Chapters,
            items:
            [
                new PlaylistItem { AbsLibraryItemId = "huge", Position = 0, BookTitle = "Huge",
                    TrackDurations = [21600], TrackBytes = [50_000_000] }, // 6h > 5h limit
            ]);

        var act = () => _sut.TransferPlaylistAsync(playlistId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*capacity*");

        var playlist = await _fixture.DbContext.Playlists.FirstAsync(p => p.Id == playlistId);
        playlist.Status.Should().Be(PlaylistStatus.Failed);
        playlist.YotoCardId.Should().BeNull();
        _yotoService.Verify(s => s.CreateOrUpdateCardAsync(
            It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
