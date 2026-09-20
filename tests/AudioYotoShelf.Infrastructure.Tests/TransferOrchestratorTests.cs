using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Core.Entities;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Data;
using AudioYotoShelf.Infrastructure.Observability;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class TransferOrchestratorTests : IDisposable
{
    private readonly InMemoryDbFixture _dbFixture;
    private readonly AudioYotoShelfDbContext _db;
    private readonly Mock<IAudiobookshelfService> _absService;
    private readonly Mock<IYotoService> _yotoService;
    private readonly Mock<IIconGenerationService> _iconService;
    private readonly Mock<IAgeSuggestionService> _ageService;
    private readonly Mock<IChapterExtractor> _chapterExtractor;
    private readonly IConfiguration _configuration;
    private readonly TransferOrchestrator _sut;
    private readonly string _tempChapterFile;

    public TransferOrchestratorTests()
    {
        _dbFixture = new InMemoryDbFixture();
        _db = _dbFixture.DbContext;
        _absService = new Mock<IAudiobookshelfService>();
        _yotoService = new Mock<IYotoService>();
        _iconService = new Mock<IIconGenerationService>();
        _ageService = new Mock<IAgeSuggestionService>();
        _chapterExtractor = new Mock<IChapterExtractor>();

        var configDict = new Dictionary<string, string?>
        {
            ["Transfer:TempDirectory"] = Path.GetTempPath()
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        _sut = new TransferOrchestrator(
            _db, _absService.Object, _yotoService.Object,
            _iconService.Object, _ageService.Object,
            _chapterExtractor.Object, Mock.Of<ITransferProgressNotifier>(), _configuration,
            new TransferMetrics(),
            Mock.Of<ILogger<TransferOrchestrator>>());

        _tempChapterFile = Path.Combine(Path.GetTempPath(), $"test_chapter_{Guid.NewGuid():N}.m4a");
        File.WriteAllBytes(_tempChapterFile, new byte[100]);

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        if (File.Exists(_tempChapterFile)) File.Delete(_tempChapterFile);
        _dbFixture.Dispose();
    }

    private void SetupDefaultMocks()
    {
        _ageService.Setup(s => s.SuggestAgeRange(It.IsAny<AbsBookMetadata>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(new AgeSuggestionResponse(5, 10, "Test", AgeRangeSource.Default, []));

        _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha256_test_hash");

        _yotoService.Setup(s => s.UploadCustomIconAsync(
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateYotoIconUpload());

        _yotoService.Setup(s => s.UploadCoverImageAsync(
                It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://covers.yotoplay.com/test.jpg");

        _yotoService.Setup(s => s.CreateOrUpdateCardAsync(
                It.IsAny<string>(), It.IsAny<YotoCardContent>(), It.IsAny<YotoCardMetadata>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("card-id-123");

        _iconService.Setup(s => s.GenerateChapterIconAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header

        // Vary by chapter title so distinct chapters hash distinctly (matches production prompt).
        _iconService.Setup(s => s.BuildChapterIconPrompt(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string chapterTitle, string bookTitle, string? genre) => $"prompt: {chapterTitle}");

        // Return a fresh stream per call — the orchestrator disposes each stream it reads.
        _absService.Setup(s => s.GetCoverImageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0xFF, 0xD8 }));

        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[100]));

        _chapterExtractor.Setup(s => s.ExtractChapterAsync(
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_tempChapterFile);
    }

    private async Task<UserConnection> SeedUserAsync()
    {
        var user = TestData.CreateUserConnection();
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    // =========================================================================
    // TransferBookAsync
    // =========================================================================

    [Fact]
    public async Task TransferBookAsync_ValidInput_CreatesCardTransferAndReturnsCompleted()
    {
        var user = await SeedUserAsync();
        var item = TestData.CreateAbsLibraryItem();

        _absService.Setup(s => s.GetLibraryItemAsync(
                user.AudiobookshelfUrl, user.AudiobookshelfToken!, "item-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var request = TestData.CreateTransferRequest();
        var result = await _sut.TransferBookAsync(user.Id, request);

        result.Status.Should().Be(TransferStatus.Completed);
        result.BookTitle.Should().Be("Test Book");
        result.YotoCardId.Should().Be("card-id-123");
        result.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public async Task TransferBookAsync_RetryAfterFailure_ClearsStaleErrorMessage()
    {
        var user = await SeedUserAsync();
        var transferId = Guid.NewGuid();
        _db.CardTransfers.Add(new CardTransfer
        {
            Id = transferId,
            UserConnectionId = user.Id,
            AbsLibraryItemId = "item-123",
            BookTitle = "Previously failed",
            AgeSuggestionReason = "n/a",
            Status = TransferStatus.Failed,
            ProgressPercent = 85,
            ErrorMessage = "Response status code does not indicate success: 400 (Bad Request)."
        });
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), "item-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem());

        var result = await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(), transferId);

        result.Status.Should().Be(TransferStatus.Completed);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task TransferBookAsync_WithAgeOverride_StoresOverrideValues()
    {
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem());

        var request = new CreateTransferRequest("item-123", OverrideMinAge: 3, OverrideMaxAge: 7);
        var result = await _sut.TransferBookAsync(user.Id, request);

        result.AgeRange.OverrideMin.Should().Be(3);
        result.AgeRange.OverrideMax.Should().Be(7);
        result.AgeRange.EffectiveMin.Should().Be(3);
        result.AgeRange.EffectiveMax.Should().Be(7);
    }

    [Fact]
    public async Task TransferBookAsync_UserNotFound_Throws()
    {
        var request = TestData.CreateTransferRequest();
        var act = () => _sut.TransferBookAsync(Guid.NewGuid(), request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task TransferBookAsync_NoAbsConnection_Throws()
    {
        var user = TestData.CreateUserConnection(absToken: "token");
        user.AudiobookshelfToken = null;
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Audiobookshelf*");
    }

    [Fact]
    public async Task TransferBookAsync_NoYotoConnection_Throws()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Yoto*");
    }

    [Fact]
    public async Task TransferBookAsync_AbsServiceFails_SetsStatusToFailed()
    {
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ABS unreachable"));

        var act = () => _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        await act.Should().ThrowAsync<HttpRequestException>();

        var transfer = await _db.CardTransfers.FirstOrDefaultAsync();
        transfer.Should().NotBeNull();
        transfer!.Status.Should().Be(TransferStatus.Failed);
        transfer.ErrorMessage.Should().Contain("ABS unreachable");
    }

    [Fact]
    public async Task TransferBookAsync_CallsAgeService_WithCorrectMetadata()
    {
        var user = await SeedUserAsync();
        var media = TestData.CreateAbsMedia(
            metadata: TestData.CreateAbsMetadata(genres: ["Fantasy"]),
            duration: 7200, numChapters: 15);
        var item = TestData.CreateAbsLibraryItem(media: media);

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _ageService.Verify(s => s.SuggestAgeRange(
            It.Is<AbsBookMetadata>(m => m.Genres!.Contains("Fantasy")),
            7200,
            15), Times.Once);
    }

    [Fact]
    public async Task TransferBookAsync_UploadsEachTrackToYoto()
    {
        var user = await SeedUserAsync();
        var audioFiles = new[]
        {
            TestData.CreateAbsAudioFile(0, "ino-1"),
            TestData.CreateAbsAudioFile(1, "ino-2"),
            TestData.CreateAbsAudioFile(2, "ino-3")
        };
        var chapters = audioFiles.Select((_, i) =>
            TestData.CreateAbsChapter(i, $"Chapter {i + 1}", i * 300, (i + 1) * 300)).ToArray();
        var media = TestData.CreateAbsMedia(audioFiles: audioFiles, chapters: chapters);
        var item = TestData.CreateAbsLibraryItem(media: media);

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _yotoService.Verify(s => s.UploadAndTranscodeAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task TransferBookAsync_GeneratesIconForEachChapter()
    {
        var user = await SeedUserAsync();
        var audioFiles = new[]
        {
            TestData.CreateAbsAudioFile(0, "ino-1"),
            TestData.CreateAbsAudioFile(1, "ino-2")
        };
        var chapters = new[]
        {
            TestData.CreateAbsChapter(0, "The Beginning"),
            TestData.CreateAbsChapter(1, "The End")
        };
        var item = TestData.CreateAbsLibraryItem(
            media: TestData.CreateAbsMedia(audioFiles: audioFiles, chapters: chapters));

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _iconService.Verify(s => s.GenerateChapterIconAsync(
            "The Beginning", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _iconService.Verify(s => s.GenerateChapterIconAsync(
            "The End", It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferBookAsync_CreatesCardWithCorrectStructure()
    {
        var user = await SeedUserAsync();
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem());

        await _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

        _yotoService.Verify(s => s.CreateOrUpdateCardAsync(
            It.IsAny<string>(),
            It.Is<YotoCardContent>(c => c.Chapters.Length > 0 && c.PlaybackType == "linear"),
            It.Is<YotoCardMetadata>(m => m.Author == "Test Author" && m.MinAge == 5),
            "Test Book",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // TransferSeriesAsync
    // =========================================================================

    [Fact]
    public async Task TransferSeriesAsync_TransfersEachBookInOrder()
    {
        var user = await SeedUserAsync();
        var series = TestData.CreateAbsSeriesItem("My Series", 3);

        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem());

        var result = await _sut.TransferSeriesAsync(user.Id,
            TestData.CreateSeriesTransferRequest());

        result.Should().HaveCount(3);
        result.Should().OnlyContain(r => r.Status == TransferStatus.Completed);
    }

    [Fact]
    public async Task TransferSeriesAsync_OneBookFails_ContinuesOthers()
    {
        var user = await SeedUserAsync();
        var series = TestData.CreateAbsSeriesItem("My Series", 3);

        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(series);

        var callCount = 0;
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 2) throw new HttpRequestException("Failed");
                return TestData.CreateAbsLibraryItem();
            });

        var result = await _sut.TransferSeriesAsync(user.Id,
            TestData.CreateSeriesTransferRequest());

        result.Should().HaveCount(2, "Should continue after one failure");
    }

    // =========================================================================
    // RetryTransferAsync
    // =========================================================================

    [Fact]
    public async Task RetryTransferAsync_FailedTransfer_ResetsAndRetries()
    {
        var user = await SeedUserAsync();
        var transfer = TestData.CreateCardTransfer(userConnectionId: user.Id, status: TransferStatus.Failed);
        transfer.ErrorMessage = "Previous error";
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem());

        var result = await _sut.RetryTransferAsync(transfer.Id);

        result.Status.Should().Be(TransferStatus.Completed);
    }

    [Fact]
    public async Task RetryTransferAsync_NonFailedTransfer_Throws()
    {
        var user = await SeedUserAsync();
        var transfer = TestData.CreateCardTransfer(userConnectionId: user.Id, status: TransferStatus.Completed);
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        var act = () => _sut.RetryTransferAsync(transfer.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retry failed*");
    }

    // =========================================================================
    // CancelTransferAsync
    // =========================================================================

    [Fact]
    public async Task CancelTransferAsync_SetsStatusToCancelled()
    {
        var transfer = TestData.CreateCardTransfer(status: TransferStatus.DownloadingAudio);
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        await _sut.CancelTransferAsync(transfer.Id);

        var updated = await _db.CardTransfers.FindAsync(transfer.Id);
        updated!.Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task CancelTransferAsync_NonExistentTransfer_Throws()
    {
        var act = () => _sut.CancelTransferAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // =========================================================================
    // GetTransferStatusAsync
    // =========================================================================

    [Fact]
    public async Task GetTransferStatusAsync_ReturnsCorrectResponse()
    {
        var transfer = TestData.CreateCardTransfer(status: TransferStatus.Completed);
        transfer.YotoCardId = "card-456";
        _db.CardTransfers.Add(transfer);
        await _db.SaveChangesAsync();

        var result = await _sut.GetTransferStatusAsync(transfer.Id);

        result.Id.Should().Be(transfer.Id);
        result.Status.Should().Be(TransferStatus.Completed);
        result.YotoCardId.Should().Be("card-456");
    }

    // =========================================================================
    // GetUserTransfersAsync
    // =========================================================================

    [Fact]
    public async Task GetUserTransfersAsync_ReturnsPaginatedResults()
    {
        var userId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            _db.CardTransfers.Add(TestData.CreateCardTransfer(userConnectionId: userId));
        }
        await _db.SaveChangesAsync();

        var result = await _sut.GetUserTransfersAsync(userId, page: 0, limit: 3);

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetUserTransfersAsync_OnlyReturnsCurrentUser()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        _db.CardTransfers.Add(TestData.CreateCardTransfer(userConnectionId: userId));
        _db.CardTransfers.Add(TestData.CreateCardTransfer(userConnectionId: otherUserId));
        await _db.SaveChangesAsync();

        var result = await _sut.GetUserTransfersAsync(userId);

        result.Should().HaveCount(1);
    }

    // =========================================================================
    // ParseSequence (static helper)
    // =========================================================================

    [Theory]
    [InlineData("1", 1f)]
    [InlineData("2.5", 2.5f)]
    [InlineData("10", 10f)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void ParseSequence_HandlesVariousFormats(string? input, float? expected)
    {
        TransferOrchestrator.ParseSequence(input).Should().Be(expected);
    }

    // =========================================================================
    // Mutation-testing additions — cross-user isolation of reused uploads and icons
    // =========================================================================

    private void ServeItem(string itemId, AbsBookMedia? media = null) =>
        _absService.Setup(s => s.GetLibraryItemAsync(
                It.IsAny<string>(), It.IsAny<string>(), itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsLibraryItem(itemId, media));

    private Task<TransferResponse> TransferAsync(UserConnection user, string itemId) =>
        _sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest(itemId));

    private void VerifyTrackUploads(Times times) =>
        _yotoService.Verify(s => s.UploadAndTranscodeAsync(
            It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), times);

    private void VerifyIconGenerations(string chapterTitle, Times times) =>
        _iconService.Verify(s => s.GenerateChapterIconAsync(
            chapterTitle, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), times);

    private async Task<UserConnection> SeedUserAsync(string username)
    {
        var user = TestData.CreateUserConnection(username: username);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task TransferBookAsync_SameUserSendsTheSameAudioFileAgain_ReusesTheUpload()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a");
        ServeItem("book-b");   // a different item that shares the same audio file inode ("ino-1")

        await TransferAsync(user, "book-a");
        await TransferAsync(user, "book-b");

        VerifyTrackUploads(Times.Once());
        var second = await _db.CardTransfers.Include(t => t.TrackMappings).SingleAsync(t => t.AbsLibraryItemId == "book-b");
        second.TrackMappings.Single().YotoTrackUrl.Should().Be("yoto:#sha256_test_hash");
    }

    [Fact]
    public async Task TransferBookAsync_AnotherUsersUploadOfTheSameInode_IsNeverReused()
    {
        // Yoto media is account-scoped and inodes can collide across Audiobookshelf servers.
        var first = await SeedUserAsync("first");
        var second = await SeedUserAsync("second");
        ServeItem("book-a");

        await TransferAsync(first, "book-a");
        await TransferAsync(second, "book-a");

        VerifyTrackUploads(Times.Exactly(2));
    }

    [Fact]
    public async Task TransferBookAsync_SameUserSecondTransfer_ReusesTheirIconAndCountsTheUse()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a");
        ServeItem("book-b");

        await TransferAsync(user, "book-a");
        await TransferAsync(user, "book-b");

        VerifyIconGenerations("Chapter 1", Times.Once());
        (await _db.GeneratedIcons.SingleAsync()).TimesUsed.Should().Be(2);
    }

    [Fact]
    public async Task TransferBookAsync_AnotherUsersIdenticalIcon_IsNeverReused()
    {
        var first = await SeedUserAsync("first");
        var second = await SeedUserAsync("second");
        ServeItem("book-a");

        await TransferAsync(first, "book-a");
        await TransferAsync(second, "book-a");

        VerifyIconGenerations("Chapter 1", Times.Exactly(2));
        (await _db.GeneratedIcons.Select(i => i.UserConnectionId).Distinct().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task TransferBookAsync_ADifferentChapterTitle_GetsANewIconEvenForTheSameUser()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a");
        ServeItem("book-b", TestData.CreateAbsMedia(chapters: [TestData.CreateAbsChapter(0, "Other")]));

        await TransferAsync(user, "book-a");
        await TransferAsync(user, "book-b");

        VerifyIconGenerations("Chapter 1", Times.Once());
        VerifyIconGenerations("Other", Times.Once());
    }

    [Fact]
    public async Task TransferBookAsync_StoredIconWithoutAYotoMediaId_IsRegenerated()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a");
        ServeItem("book-b");
        await TransferAsync(user, "book-a");
        foreach (var icon in _db.GeneratedIcons) icon.YotoMediaId = null;   // never made it to Yoto
        await _db.SaveChangesAsync();

        await TransferAsync(user, "book-b");

        VerifyIconGenerations("Chapter 1", Times.Exactly(2));
    }

    [Fact]
    public async Task TransferBookAsync_TwoChaptersWithTheSameTitle_ShareOneIconWithinTheRun()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1"), TestData.CreateAbsAudioFile(1, "ino-2")],
            chapters: [TestData.CreateAbsChapter(0, "Same", 0, 300), TestData.CreateAbsChapter(1, "Same", 300, 600)]));

        await TransferAsync(user, "book-a");

        VerifyIconGenerations("Same", Times.Once());
        (await _db.GeneratedIcons.SingleAsync()).TimesUsed.Should().Be(2);
    }

    private List<YotoCardContent> CardsSentToYoto() =>
        _yotoService.Invocations
            .Where(i => i.Method.Name == nameof(IYotoService.CreateOrUpdateCardAsync))
            .Select(i => (YotoCardContent)i.Arguments[1])
            .ToList();

    [Fact]
    public async Task TransferBookAsync_TwoChaptersSharingAnIcon_BothReferenceItOnTheCard()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a", TestData.CreateAbsMedia(
            audioFiles: [TestData.CreateAbsAudioFile(0, "ino-1"), TestData.CreateAbsAudioFile(1, "ino-2")],
            chapters: [TestData.CreateAbsChapter(0, "Same", 0, 300), TestData.CreateAbsChapter(1, "Same", 300, 600)]));

        await TransferAsync(user, "book-a");

        CardsSentToYoto().Single().Chapters.Select(c => c.Display?.Icon16X16)
            .Should().Equal("yoto:#icon-media-1", "yoto:#icon-media-1");
    }

    [Fact]
    public async Task TransferBookAsync_ReusedIconFromAnEarlierTransfer_IsReferencedOnTheNewCard()
    {
        var user = await SeedUserAsync();
        ServeItem("book-a");
        ServeItem("book-b");

        await TransferAsync(user, "book-a");
        await TransferAsync(user, "book-b");

        CardsSentToYoto().Last().Chapters.Single().Display!.Icon16X16.Should().Be("yoto:#icon-media-1");
    }

    // --- TransferSeriesAsync guards

    [Fact]
    public async Task TransferSeriesAsync_UnknownUser_Throws()
    {
        var act = () => _sut.TransferSeriesAsync(Guid.NewGuid(), TestData.CreateSeriesTransferRequest());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("User connection not found");
    }

    [Fact]
    public async Task TransferSeriesAsync_UserWithoutAYotoConnection_ThrowsBeforeTouchingTheSeries()
    {
        var user = TestData.CreateUserConnection(yotoAccessToken: null);
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsSeriesItem("Saga", 2));

        var act = () => _sut.TransferSeriesAsync(user.Id, TestData.CreateSeriesTransferRequest());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Yoto*");
    }

    [Fact]
    public async Task TransferSeriesAsync_RefreshesAnExpiringAudiobookshelfTokenBeforeReadingTheSeries()
    {
        var user = TestData.CreateUserConnection(
            absToken: "stale", absRefreshToken: "refresh-old", absTokenExpiry: DateTimeOffset.UtcNow.AddMinutes(1));
        _db.UserConnections.Add(user);
        await _db.SaveChangesAsync();
        _absService.Setup(s => s.RefreshTokenAsync(user.AudiobookshelfUrl, "refresh-old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AbsLoginResponse(
                new AbsUser("u1", "testuser", "user", "legacy", true, null, null, "renewed", "refresh-new"), null));
        _absService.Setup(s => s.GetSeriesDetailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestData.CreateAbsSeriesItem("Saga", 0));

        await _sut.TransferSeriesAsync(user.Id, TestData.CreateSeriesTransferRequest());

        _absService.Verify(s => s.GetSeriesDetailAsync(
            user.AudiobookshelfUrl, "renewed", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- GetUserTransfersAsync ordering and paging

    [Fact]
    public async Task GetUserTransfersAsync_ReturnsNewestFirst_AndPagesByLimit()
    {
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var transfer = TestData.CreateCardTransfer(userConnectionId: userId, title: $"t{i}");
            transfer.CreatedAt = now.AddHours(-i);   // t0 is the newest
            _db.CardTransfers.Add(transfer);
        }
        await _db.SaveChangesAsync();

        var firstPage = await _sut.GetUserTransfersAsync(userId, page: 0, limit: 2);
        var secondPage = await _sut.GetUserTransfersAsync(userId, page: 1, limit: 2);

        firstPage.Select(t => t.BookTitle).Should().Equal("t0", "t1");
        secondPage.Select(t => t.BookTitle).Should().Equal("t2", "t3");
    }
}
