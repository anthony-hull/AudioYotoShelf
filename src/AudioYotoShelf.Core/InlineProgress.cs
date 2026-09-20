namespace AudioYotoShelf.Core;

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> posts each report to the thread pool,
/// where two can run out of order; anything shown to a person as a sequence needs this instead.
/// </summary>
public sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
