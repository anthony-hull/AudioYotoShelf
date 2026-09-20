using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AudioYotoShelf.Core.Interfaces;
using AudioYotoShelf.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

/// <summary>
/// Every test runs against <see cref="FakeProcessRunner"/>: no test starts a real process, and none needs
/// ffmpeg installed. The fake plays the part of ffmpeg by creating the files it would have written.
/// </summary>
public class FfmpegChapterExtractorTests : IDisposable
{
    private const string TempFileNamePattern = "^{0}_[0-9a-f]{{32}}\\.{1}$";

    private readonly FakeProcessRunner _runner = new();
    private readonly FfmpegChapterExtractor _sut;
    private readonly List<string> _filesToDelete = [];
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    public FfmpegChapterExtractorTests()
    {
        // ffmpeg arguments format numbers with the current culture; pin it so "12.500" is not "12,500".
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _sut = new FfmpegChapterExtractor(Mock.Of<ILogger<FfmpegChapterExtractor>>(), _runner);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        foreach (var file in _filesToDelete.Where(File.Exists))
            File.Delete(file);
    }

    private string NewInputFile(string? fileName = null)
    {
        var path = fileName is null
            ? Path.GetTempFileName()
            : Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{fileName}");
        if (fileName is not null) File.WriteAllText(path, "");
        _filesToDelete.Add(path);
        return path;
    }

    /// <summary>What ffmpeg does for extract and concat: write an output file at the last quoted path.</summary>
    private void FfmpegWritesItsOutputFile() => _runner.OnRun = args =>
    {
        var output = QuotedPaths(args).Last();
        File.WriteAllBytes(output, [1, 2, 3]);
        _filesToDelete.Add(output);
    };

    private static string[] QuotedPaths(string args) =>
        Regex.Matches(args, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();

    private static void AssertIsTempFile(string path, string prefix, string extension)
    {
        Path.GetDirectoryName(path).Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        Path.GetFileName(path).Should().MatchRegex(string.Format(TempFileNamePattern, prefix, extension));
    }

    // =========================================================================
    // Argument validation
    // =========================================================================

    [Fact]
    public async Task ExtractChapterAsync_NullInputPath_Throws()
    {
        var act = () => _sut.ExtractChapterAsync(null!, 0, 300, "m4a");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("inputFilePath");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractChapterAsync_NonExistentFile_NamesTheMissingFile()
    {
        var act = () => _sut.ExtractChapterAsync("/nonexistent/file.m4b", 0, 300, "m4a");

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.FileName.Should().Be("/nonexistent/file.m4b");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractChapterAsync_NegativeStart_BlamesTheStartArgument()
    {
        var act = () => _sut.ExtractChapterAsync(NewInputFile(), -0.001, 300, "m4a");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("startSeconds");
        _runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(300, 300)]
    [InlineData(300, 100)]
    public async Task ExtractChapterAsync_EndNotAfterStart_BlamesTheEndArgument(double start, double end)
    {
        var act = () => _sut.ExtractChapterAsync(NewInputFile(), start, end, "m4a");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("endSeconds");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SplitAsync_NullInputPath_Throws()
    {
        var act = () => _sut.SplitAsync(null!, 60);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("inputFilePath");
    }

    [Fact]
    public async Task SplitAsync_NonExistentFile_NamesTheMissingFile()
    {
        var act = () => _sut.SplitAsync("/nonexistent/file.m4b", 60);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.FileName.Should().Be("/nonexistent/file.m4b");
        _runner.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task SplitAsync_SegmentLengthNotPositive_BlamesTheSegmentArgument(double segmentSeconds)
    {
        var act = () => _sut.SplitAsync(NewInputFile(), segmentSeconds);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("segmentSeconds");
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcatenateAsync_NoInputs_Throws()
    {
        var act = () => _sut.ConcatenateAsync([]);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("inputFilePaths");
    }

    [Fact]
    public async Task ConcatenateAsync_AnyInputMissing_NamesItAndRunsNothing()
    {
        var act = () => _sut.ConcatenateAsync([NewInputFile(), "/nonexistent/second.m4a"]);

        (await act.Should().ThrowAsync<FileNotFoundException>()).Which.FileName.Should().Be("/nonexistent/second.m4a");
        _runner.Calls.Should().BeEmpty();
    }

    // =========================================================================
    // ExtractChapterAsync
    // =========================================================================

    [Fact]
    public async Task ExtractChapterAsync_CutsTheChapterBoundsWithoutReencoding()
    {
        var input = NewInputFile();
        FfmpegWritesItsOutputFile();

        var output = await _sut.ExtractChapterAsync(input, 12.5, 300.25, "m4a");

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().Be($"-i \"{input}\" -ss 12.500 -to 300.250 -c copy -y \"{output}\"");
        call.CaptureStandardError.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractChapterAsync_FirstChapterStartsAtZero()
    {
        var input = NewInputFile();
        FfmpegWritesItsOutputFile();

        await _sut.ExtractChapterAsync(input, 0, 60, "m4a");

        _runner.Calls.Single().Arguments.Should().Contain(" -ss 0.000 -to 60.000 ");
    }

    [Fact]
    public async Task ExtractChapterAsync_WritesToAFreshTempFileWithTheRequestedFormat()
    {
        var input = NewInputFile();
        FfmpegWritesItsOutputFile();

        var first = await _sut.ExtractChapterAsync(input, 0, 60, "mp3");
        var second = await _sut.ExtractChapterAsync(input, 0, 60, "mp3");

        AssertIsTempFile(first, "chapter", "mp3");
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task ExtractChapterAsync_DefaultsToM4a()
    {
        FfmpegWritesItsOutputFile();

        var output = await _sut.ExtractChapterAsync(NewInputFile(), 0, 60);

        AssertIsTempFile(output, "chapter", "m4a");
    }

    [Fact]
    public async Task ExtractChapterAsync_FfmpegFails_ThrowsWithItsErrorOutput()
    {
        _runner.Result = new ProcessResult(1, "Invalid data found when processing input");

        var act = () => _sut.ExtractChapterAsync(NewInputFile(), 0, 60, "m4a");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("FFmpeg chapter extraction failed: Invalid data found when processing input");
    }

    [Fact]
    public async Task ExtractChapterAsync_PassesTheCancellationTokenToFfmpeg()
    {
        FfmpegWritesItsOutputFile();
        using var cts = new CancellationTokenSource();

        await _sut.ExtractChapterAsync(NewInputFile(), 0, 60, "m4a", cts.Token);

        _runner.Calls.Single().Token.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ExtractChapterAsync_Cancelled_PropagatesTheCancellation()
    {
        _runner.Failure = new OperationCanceledException();

        var act = () => _sut.ExtractChapterAsync(NewInputFile(), 0, 60, "m4a");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // =========================================================================
    // ConcatenateAsync
    // =========================================================================

    [Fact]
    public async Task ConcatenateAsync_SingleFile_ReturnsItUntouchedWithoutRunningFfmpeg()
    {
        var input = NewInputFile();

        var output = await _sut.ConcatenateAsync([input]);

        output.Should().Be(input);
        _runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcatenateAsync_SeveralFiles_JoinsThemInOrderWithTheConcatDemuxer()
    {
        var first = NewInputFile();
        var second = NewInputFile();
        string? listContent = null;
        string? listPath = null;
        _runner.OnRun = args =>
        {
            listPath = QuotedPaths(args)[0];
            listContent = File.ReadAllText(listPath);
            File.WriteAllBytes(QuotedPaths(args)[1], [1]);
            _filesToDelete.Add(QuotedPaths(args)[1]);
        };

        var output = await _sut.ConcatenateAsync([first, second], "mp3");

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().Be($"-f concat -safe 0 -i \"{listPath}\" -c copy -y \"{output}\"");
        call.CaptureStandardError.Should().BeTrue();
        listContent.Should().Be($"file '{first}'\nfile '{second}'");
        AssertIsTempFile(output, "audio", "mp3");
        AssertIsTempFile(listPath!, "concat", "txt");
    }

    [Fact]
    public async Task ConcatenateAsync_DefaultsToM4a()
    {
        _runner.OnRun = args => _filesToDelete.Add(QuotedPaths(args)[1]);

        var output = await _sut.ConcatenateAsync([NewInputFile(), NewInputFile()]);

        AssertIsTempFile(output, "audio", "m4a");
    }

    [Fact]
    public async Task ConcatenateAsync_EscapesApostrophesInTheListFile()
    {
        var quoted = NewInputFile("it's.m4a");
        var plain = NewInputFile();
        string? listContent = null;
        _runner.OnRun = args => listContent = File.ReadAllText(QuotedPaths(args)[0]);

        await _sut.ConcatenateAsync([quoted, plain]);

        listContent.Should().Be($"file '{quoted.Replace("'", "'\\''")}'\nfile '{plain}'");
    }

    [Fact]
    public async Task ConcatenateAsync_RemovesTheListFileAfterSuccess()
    {
        string? listPath = null;
        _runner.OnRun = args => listPath = QuotedPaths(args)[0];

        await _sut.ConcatenateAsync([NewInputFile(), NewInputFile()]);

        File.Exists(listPath).Should().BeFalse();
    }

    [Fact]
    public async Task ConcatenateAsync_FfmpegFails_ThrowsAndStillRemovesTheListFile()
    {
        string? listPath = null;
        _runner.OnRun = args => listPath = QuotedPaths(args)[0];
        _runner.Result = new ProcessResult(1, "Impossible to open");

        var act = () => _sut.ConcatenateAsync([NewInputFile(), NewInputFile()]);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("FFmpeg concatenation failed: Impossible to open");
        File.Exists(listPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExtractChapterAsync_WritesSecondsWithAPointEvenUnderACommaDecimalCulture()
    {
        CultureInfo.CurrentCulture = CommaDecimalCulture();
        var input = NewInputFile();
        FfmpegWritesItsOutputFile();

        await _sut.ExtractChapterAsync(input, 12.5, 300.25, "m4a");

        _runner.Calls.Should().ContainSingle().Which.Arguments.Should().Contain("-ss 12.500 -to 300.250 ");
    }

    private static CultureInfo CommaDecimalCulture()
    {
        // Built from the invariant culture so it does not depend on the machine's ICU data.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    // =========================================================================
    // SplitAsync
    // =========================================================================

    [Fact]
    public async Task SplitAsync_WritesTheSegmentLengthWithAPointEvenUnderACommaDecimalCulture()
    {
        CultureInfo.CurrentCulture = CommaDecimalCulture();
        var input = NewInputFile();
        _runner.OnRun = args => CreateSegments(QuotedPaths(args)[1], "000");

        await _sut.SplitAsync(input, 90.5);

        _runner.Calls.Should().ContainSingle().Which.Arguments.Should().Contain("-segment_time 90.500 ");
    }

    [Fact]
    public async Task SplitAsync_UsesTheSegmentMuxerWithTheSegmentLengthAndStreamCopy()
    {
        var input = NewInputFile();
        string? pattern = null;
        _runner.OnRun = args =>
        {
            pattern = QuotedPaths(args)[1];
            CreateSegments(pattern, "000");
        };

        await _sut.SplitAsync(input, 90.5);

        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().Be(
            $"-i \"{input}\" -f segment -segment_time 90.500 -c copy -reset_timestamps 1 -y \"{pattern}\"");
        call.CaptureStandardError.Should().BeTrue();
        Path.GetDirectoryName(pattern).Should().Be(Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        Path.GetFileName(pattern).Should().MatchRegex("^segment_[0-9a-f]{32}_%03d\\.m4a$");
    }

    [Fact]
    public async Task SplitAsync_ReturnsTheSegmentsInOrder()
    {
        _runner.OnRun = args => CreateSegments(QuotedPaths(args)[1], "002", "000", "001");

        var segments = await _sut.SplitAsync(NewInputFile(), 60);

        segments.Select(Path.GetFileName).Select(name => name![^7..]).Should().Equal("000.m4a", "001.m4a", "002.m4a");
    }

    [Fact]
    public async Task SplitAsync_ReturnsOnlyThisRunsSegmentsInTheRequestedFormat()
    {
        _runner.OnRun = args =>
        {
            var pattern = QuotedPaths(args)[1];
            CreateSegments(pattern, "000", "001");
            // Same run, other format; and another run's segment in the same directory.
            CreateFile(pattern.Replace("_%03d.m4a", "_000.wav"));
            CreateFile(Path.Combine(Path.GetTempPath(), $"segment_{Guid.NewGuid():N}_000.m4a"));
        };

        var segments = await _sut.SplitAsync(NewInputFile(), 60, "m4a");

        segments.Should().HaveCount(2).And.OnlyContain(s => s.EndsWith(".m4a"));
    }

    [Fact]
    public async Task SplitAsync_UsesTheRequestedFormatForTheSegments()
    {
        string? pattern = null;
        _runner.OnRun = args =>
        {
            pattern = QuotedPaths(args)[1];
            CreateSegments(pattern, "000");
        };

        var segments = await _sut.SplitAsync(NewInputFile(), 60, "mp3");

        pattern.Should().EndWith("_%03d.mp3");
        segments.Should().ContainSingle().Which.Should().EndWith("_000.mp3");
    }

    [Fact]
    public async Task SplitAsync_FfmpegWritesNothing_Throws()
    {
        var act = () => _sut.SplitAsync(NewInputFile(), 60);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("FFmpeg split produced no segments");
    }

    [Fact]
    public async Task SplitAsync_FfmpegFails_ThrowsWithItsErrorOutput()
    {
        _runner.Result = new ProcessResult(1, "Output file is empty");

        var act = () => _sut.SplitAsync(NewInputFile(), 60);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("FFmpeg split failed: Output file is empty");
    }

    // =========================================================================
    // IsFfmpegAvailableAsync
    // =========================================================================

    [Fact]
    public async Task IsFfmpegAvailableAsync_ExitZero_IsAvailable()
    {
        using var cts = new CancellationTokenSource();

        var available = await _sut.IsFfmpegAvailableAsync(cts.Token);

        available.Should().BeTrue();
        var call = _runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("ffmpeg");
        call.Arguments.Should().Be("-version");
        call.CaptureStandardError.Should().BeFalse();
        call.Token.Should().Be(cts.Token);
    }

    [Fact]
    public async Task IsFfmpegAvailableAsync_NonZeroExit_IsNotAvailable()
    {
        _runner.Result = new ProcessResult(1, "");

        (await _sut.IsFfmpegAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsFfmpegAvailableAsync_ProgramCannotBeStarted_IsNotAvailable()
    {
        _runner.Failure = new Win32Exception("No such file or directory");

        (await _sut.IsFfmpegAvailableAsync()).Should().BeFalse();
    }

    // =========================================================================
    // Wiring
    // =========================================================================

    [Fact]
    public void Extractor_ResolvesFromTheContainerWithTheSystemProcessRunner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddScoped<IChapterExtractor, FfmpegChapterExtractor>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChapterExtractor>().Should().BeOfType<FfmpegChapterExtractor>();
    }

    // =========================================================================
    // Test doubles and helpers
    // =========================================================================

    private void CreateSegments(string pattern, params string[] numbers)
    {
        foreach (var number in numbers)
            CreateFile(pattern.Replace("%03d", number));
    }

    private void CreateFile(string path)
    {
        File.WriteAllBytes(path, [1]);
        _filesToDelete.Add(path);
    }

    private sealed record Call(string FileName, string Arguments, bool CaptureStandardError, CancellationToken Token);

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<Call> Calls { get; } = [];
        public ProcessResult Result { get; set; } = new(0, "");
        public Exception? Failure { get; set; }
        public Action<string>? OnRun { get; set; }

        public Task<ProcessResult> RunAsync(
            string fileName, string arguments, bool captureStandardError, CancellationToken ct)
        {
            Calls.Add(new Call(fileName, arguments, captureStandardError, ct));
            if (Failure is not null) return Task.FromException<ProcessResult>(Failure);

            OnRun?.Invoke(arguments);
            return Task.FromResult(Result);
        }
    }
}
