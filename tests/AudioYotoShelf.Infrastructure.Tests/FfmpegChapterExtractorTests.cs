using AudioYotoShelf.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

public class FfmpegChapterExtractorTests
{
    private readonly FfmpegChapterExtractor _sut;

    public FfmpegChapterExtractorTests()
    {
        _sut = new FfmpegChapterExtractor(Mock.Of<ILogger<FfmpegChapterExtractor>>());
    }

    [Fact]
    public async Task IsFfmpegAvailableAsync_ReturnsBoolean()
    {
        // This test verifies the method doesn't throw and returns a definitive answer.
        // On CI without FFmpeg it returns false; locally with FFmpeg it returns true.
        var result = await _sut.IsFfmpegAvailableAsync();
        new[] { true, false }.Should().Contain(result);
    }

    [Fact]
    public async Task ExtractChapterAsync_NullInputPath_Throws()
    {
        var act = () => _sut.ExtractChapterAsync(null!, 0, 300, "m4a");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExtractChapterAsync_NonExistentFile_Throws()
    {
        var act = () => _sut.ExtractChapterAsync("/nonexistent/file.m4b", 0, 300, "m4a");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ExtractChapterAsync_NegativeStartTime_Throws()
    {
        // Create a temp file so the file-exists check passes
        var tempFile = Path.GetTempFileName();
        try
        {
            var act = () => _sut.ExtractChapterAsync(tempFile, -5, 300, "m4a");
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractChapterAsync_EndBeforeStart_Throws()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var act = () => _sut.ExtractChapterAsync(tempFile, 300, 100, "m4a");
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // =========================================================================
    // Mutation-testing additions: argument validation only. Everything past validation runs the
    // real `ffmpeg` binary by name (no seam), so those paths are covered by integration only.
    // =========================================================================

    [Fact]
    public async Task ExtractChapterAsync_NonExistentFile_NamesTheMissingFile()
    {
        var act = () => _sut.ExtractChapterAsync("/nonexistent/file.m4b", 0, 300, "m4a");

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.FileName.Should().Be("/nonexistent/file.m4b");
    }

    [Fact]
    public async Task ExtractChapterAsync_NegativeStart_BlamesTheStartArgument()
    {
        using var input = TempInputFile.Create();

        var act = () => _sut.ExtractChapterAsync(input.Path, -0.001, 300, "m4a");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("startSeconds");
    }

    [Fact]
    public async Task ExtractChapterAsync_EndEqualToStart_BlamesTheEndArgument()
    {
        using var input = TempInputFile.Create();

        var act = () => _sut.ExtractChapterAsync(input.Path, 300, 300, "m4a");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("endSeconds");
    }

    [Fact]
    public async Task ExtractChapterAsync_StartOfZero_IsAValidStartAndPassesValidation()
    {
        // The first chapter of every book starts at 0. Validation passes, so the failure (if any) comes
        // from running ffmpeg on an empty file, or from ffmpeg being absent: never an ArgumentException.
        using var input = TempInputFile.Create();

        var error = await Record.ExceptionAsync(() => _sut.ExtractChapterAsync(input.Path, 0, 300, "m4a"));

        error.Should().NotBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public async Task SplitAsync_NullInputPath_Throws()
    {
        var act = () => _sut.SplitAsync(null!, 60);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SplitAsync_NonExistentFile_NamesTheMissingFile()
    {
        var act = () => _sut.SplitAsync("/nonexistent/file.m4b", 60);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.FileName.Should().Be("/nonexistent/file.m4b");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task SplitAsync_SegmentLengthNotPositive_BlamesTheSegmentArgument(double segmentSeconds)
    {
        using var input = TempInputFile.Create();

        var act = () => _sut.SplitAsync(input.Path, segmentSeconds);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("segmentSeconds");
    }

    [Fact]
    public async Task SplitAsync_PositiveSegmentLength_PassesValidation()
    {
        using var input = TempInputFile.Create();

        var error = await Record.ExceptionAsync(() => _sut.SplitAsync(input.Path, 30));

        error.Should().NotBeAssignableTo<ArgumentException>();
    }

    private sealed class TempInputFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetTempFileName();

        public static TempInputFile Create() => new();

        public void Dispose() => File.Delete(Path);
    }
}
