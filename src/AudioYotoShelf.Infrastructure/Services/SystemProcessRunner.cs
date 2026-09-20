using System.Diagnostics;

namespace AudioYotoShelf.Infrastructure.Services;

/// <summary>The real <see cref="IProcessRunner"/>: starts an operating-system process. Not unit-tested by design.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName, string arguments, bool captureStandardError, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stderr = captureStandardError ? await process.StandardError.ReadToEndAsync(ct) : "";
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, stderr);
    }
}
