using AudioYotoShelf.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AudioYotoShelf.Infrastructure.Services;

public class FfmpegChapterExtractor(ILogger<FfmpegChapterExtractor> logger, IProcessRunner processRunner) : IChapterExtractor
{
    public async Task<string> ExtractChapterAsync(
        string inputFilePath, double startSeconds, double endSeconds,
        string outputFormat = "m4a", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputFilePath, nameof(inputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("Input audio file not found", inputFilePath);
        if (startSeconds < 0)
            throw new ArgumentException("Start time cannot be negative", nameof(startSeconds));
        if (endSeconds <= startSeconds)
            throw new ArgumentException("End time must be greater than start time", nameof(endSeconds));

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"chapter_{Guid.NewGuid():N}.{outputFormat}");

        // ffmpeg parses a decimal point only; the current culture must not turn 12.500 into 12,500.
        var args = FormattableString.Invariant(
            $"-i \"{inputFilePath}\" -ss {startSeconds:F3} -to {endSeconds:F3} -c copy -y \"{outputPath}\"");

        await RunFfmpegAsync(args, "chapter extraction", ct);

        logger.LogInformation("Chapter extracted to {OutputPath} ({Size} bytes)",
            outputPath, new FileInfo(outputPath).Length);

        return outputPath;
    }

    public async Task<string> ConcatenateAsync(
        IReadOnlyList<string> inputFilePaths, string outputFormat = "m4a", CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(inputFilePaths.Count, nameof(inputFilePaths));
        foreach (var path in inputFilePaths)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Input audio file not found", path);
        }

        if (inputFilePaths.Count == 1)
            return inputFilePaths[0];

        var outputPath = TempPath(outputFormat);

        // ffmpeg concat demuxer needs a list file of the inputs.
        var listPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        var listContent = string.Join('\n', inputFilePaths.Select(p => $"file '{p.Replace("'", "'\\''")}'"));
        await File.WriteAllTextAsync(listPath, listContent, ct);

        try
        {
            var args = $"-f concat -safe 0 -i \"{listPath}\" -c copy -y \"{outputPath}\"";
            await RunFfmpegAsync(args, "concatenation", ct);
            return outputPath;
        }
        finally
        {
            if (File.Exists(listPath)) File.Delete(listPath);
        }
    }

    public async Task<IReadOnlyList<string>> SplitAsync(
        string inputFilePath, double segmentSeconds, string outputFormat = "m4a", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputFilePath, nameof(inputFilePath));
        if (!File.Exists(inputFilePath))
            throw new FileNotFoundException("Input audio file not found", inputFilePath);
        if (segmentSeconds <= 0)
            throw new ArgumentException("Segment length must be positive", nameof(segmentSeconds));

        var token = Guid.NewGuid().ToString("N");
        var pattern = Path.Combine(Path.GetTempPath(), $"segment_{token}_%03d.{outputFormat}");

        var args = FormattableString.Invariant($"-i \"{inputFilePath}\" -f segment -segment_time {segmentSeconds:F3} ") +
                   $"-c copy -reset_timestamps 1 -y \"{pattern}\"";
        await RunFfmpegAsync(args, "split", ct);

        var segments = Directory
            .GetFiles(Path.GetTempPath(), $"segment_{token}_*.{outputFormat}")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (segments.Count == 0)
            throw new InvalidOperationException("FFmpeg split produced no segments");

        return segments;
    }

    private static string TempPath(string outputFormat) =>
        Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid():N}.{outputFormat}");

    private async Task RunFfmpegAsync(string args, string operation, CancellationToken ct)
    {
        logger.LogInformation("ffmpeg {Operation}: {Args}", operation, args);

        var result = await processRunner.RunAsync("ffmpeg", args, captureStandardError: true, ct);

        if (result.ExitCode != 0)
        {
            logger.LogError("FFmpeg {Operation} failed with exit code {ExitCode}: {Stderr}",
                operation, result.ExitCode, result.StandardError);
            throw new InvalidOperationException($"FFmpeg {operation} failed: {result.StandardError}");
        }
    }

    public async Task<bool> IsFfmpegAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await processRunner.RunAsync("ffmpeg", "-version", captureStandardError: false, ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
