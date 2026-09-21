using AudioYotoShelf.Core.Tests.Helpers;
using AudioYotoShelf.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

// A single-file book is split into one temp file per chapter. Those files are named by the extractor,
// not by the transfer, so the transfer's own clean-up (files named "<transferId>*") never found them and
// every such book left its whole audio behind in the temp volume.
public partial class TransferOrchestratorTests
{
    private const int ChaptersInSingleFileBook = 3;

    /// <summary>Makes the extractor write a real file per chapter, as ffmpeg does, and returns the paths it hands out.</summary>
    private List<string> ExtractorWritesFilesIn(string directory, int failOnCall = 0)
    {
        var extracted = new List<string>();
        _chapterExtractor.Setup(c => c.ExtractChapterAsync(
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (extracted.Count + 1 == failOnCall) throw new InvalidOperationException("ffmpeg failed");
                var path = Path.Combine(directory, $"chapter_{Guid.NewGuid():N}.m4a");
                File.WriteAllBytes(path, [1, 2, 3]);
                extracted.Add(path);
                return path;
            });
        return extracted;
    }

    private void ServeSingleFileBookWithChapters()
    {
        var chapters = Enumerable.Range(0, ChaptersInSingleFileBook)
            .Select(i => TestData.CreateAbsChapter(i, $"Chapter {i + 1}", i * 100, (i + 1) * 100))
            .ToArray();
        var media = TestData.CreateAbsMedia(audioFiles: Files((0, "ino-1", 300, 9_000)), chapters: chapters);
        ServeItemWith(TestData.CreateAbsLibraryItem("book-1", media));
        _absService.Setup(s => s.DownloadAudioFileAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3, 4]));
    }

    private (TransferOrchestrator Sut, string TempDirectory) CreateSutWithOwnTempDirectory()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("ays-chapters-").FullName;
        var sut = CreateSut(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transfer:TempDirectory"] = tempDirectory })
            .Build());
        return (sut, tempDirectory);
    }

    [Fact]
    public async Task TransferBookAsync_SingleFileBook_LeavesNoChapterFilesBehindWhenItCompletes()
    {
        var (sut, tempDirectory) = CreateSutWithOwnTempDirectory();
        try
        {
            var user = await SeedUserAsync();
            ServeSingleFileBookWithChapters();
            var extracted = ExtractorWritesFilesIn(tempDirectory);

            await sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

            extracted.Should().HaveCount(ChaptersInSingleFileBook);
            Directory.GetFiles(tempDirectory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TransferBookAsync_SingleFileBook_LeavesNoChapterFilesBehindWhenAnUploadFails()
    {
        var (sut, tempDirectory) = CreateSutWithOwnTempDirectory();
        try
        {
            var user = await SeedUserAsync();
            ServeSingleFileBookWithChapters();
            ExtractorWritesFilesIn(tempDirectory);
            _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                    It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                    It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Yoto refused the upload"));

            var act = () => sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

            await act.Should().ThrowAsync<InvalidOperationException>();
            Directory.GetFiles(tempDirectory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TransferBookAsync_SingleFileBook_LeavesNoChapterFilesBehindWhenItIsCancelled()
    {
        var (sut, tempDirectory) = CreateSutWithOwnTempDirectory();
        try
        {
            var user = await SeedUserAsync();
            ServeSingleFileBookWithChapters();
            ExtractorWritesFilesIn(tempDirectory);
            _yotoService.Setup(s => s.UploadAndTranscodeAsync(
                    It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(),
                    It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            var act = () => sut.TransferBookAsync(user.Id, TestData.CreateTransferRequest());

            await act.Should().ThrowAsync<OperationCanceledException>();
            Directory.GetFiles(tempDirectory).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BuildTrackMappings_WhenALaterChapterFailsToExtract_DeletesTheChaptersAlreadyExtracted()
    {
        var (sut, tempDirectory) = CreateSutWithOwnTempDirectory();
        try
        {
            var (user, transfer) = await SeedTransferAsync();
            ServeSingleFileBookWithChapters();
            var extracted = ExtractorWritesFilesIn(tempDirectory, failOnCall: 3);
            var item = TestData.CreateAbsLibraryItem("book-1", TestData.CreateAbsMedia(
                audioFiles: Files((0, "ino-1", 300, 9_000)),
                chapters: Enumerable.Range(0, ChaptersInSingleFileBook)
                    .Select(i => TestData.CreateAbsChapter(i, $"Chapter {i + 1}", i * 100, (i + 1) * 100)).ToArray()));

            var act = () => sut.BuildTrackMappingsAsync(user, item, transfer, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("ffmpeg failed");
            extracted.Should().HaveCount(2);
            extracted.Should().OnlyContain(path => !File.Exists(path));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
