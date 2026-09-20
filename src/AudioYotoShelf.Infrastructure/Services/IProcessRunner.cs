namespace AudioYotoShelf.Infrastructure.Services;

/// <summary>Exit code of a finished process, and its standard error when the caller asked for it.</summary>
public sealed record ProcessResult(int ExitCode, string StandardError);

/// <summary>
/// Starts an external program and waits for it to exit. Exists so code that shells out (ffmpeg) can be
/// tested against a fake instead of a real binary.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for it to exit.
    /// Throws if the program cannot be started (for example, it is not installed).
    /// When <paramref name="captureStandardError"/> is false, <see cref="ProcessResult.StandardError"/> is empty.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName, string arguments, bool captureStandardError, CancellationToken ct);
}
