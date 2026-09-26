using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Core.Tests.Helpers;
using FluentAssertions;

namespace AudioYotoShelf.Core.Tests;

public class ChapterTitleResolverTests
{
    // Audiobookshelf's AudioFile.Index is 1-based — confirmed live against a real book, where the
    // first file reports Index 1, not 0. Chapters is a plain 0-based array in file order.

    [Fact]
    public void ForFile_FirstFile_UsesTheFirstChapter()
    {
        var chapters = new[] { TestData.CreateAbsChapter(0, "The Boy Who Lived", 0, 100) };
        var file = TestData.CreateAbsAudioFile(1);

        ChapterTitleResolver.ForFile(chapters, file).Should().Be("The Boy Who Lived");
    }

    [Fact]
    public void ForFile_SecondFile_UsesTheSecondChapter()
    {
        var chapters = new[]
        {
            TestData.CreateAbsChapter(0, "The Boy Who Lived", 0, 100),
            TestData.CreateAbsChapter(1, "The Vanishing Glass", 100, 200),
        };
        var file = TestData.CreateAbsAudioFile(2);

        ChapterTitleResolver.ForFile(chapters, file).Should().Be("The Vanishing Glass");
    }

    [Fact]
    public void ForFile_NoChapterAtThatPosition_FallsBackToTheFilename()
    {
        var chapters = new[] { TestData.CreateAbsChapter(0, "The Boy Who Lived", 0, 100) };
        var file = TestData.CreateAbsAudioFile(2); // chapterPosition 1 is out of range

        ChapterTitleResolver.ForFile(chapters, file).Should().Be(file.Metadata.Filename);
    }

    [Fact]
    public void ForFile_NoChaptersAtAll_FallsBackToTheFilename()
    {
        var file = TestData.CreateAbsAudioFile(1);

        ChapterTitleResolver.ForFile([], file).Should().Be(file.Metadata.Filename);
    }
}
