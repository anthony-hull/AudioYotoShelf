using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.Interfaces;

namespace AudioYotoShelf.Core.Services;

public class CardCapacityCalculator(YotoCardLimits limits) : ICardCapacityCalculator
{
    public int ProjectedTrackCount(SourceTrack track)
    {
        var byDuration = (int)Math.Ceiling(track.DurationSeconds / limits.MaxTrackDurationSeconds);
        var byBytes = (int)Math.Ceiling((double)track.EstimatedBytes / limits.MaxTrackBytes);
        return Math.Max(1, Math.Max(byDuration, byBytes));
    }

    public CapacityResult Calculate(IEnumerable<BookTrackPlan> books)
    {
        var tracks = 0;
        var duration = 0.0;
        var bytes = 0L;
        string? firstOverflow = null;

        foreach (var book in books)
        {
            foreach (var track in book.Tracks)
            {
                tracks += ProjectedTrackCount(track);
                duration += track.DurationSeconds;
                bytes += track.EstimatedBytes;
            }

            if (firstOverflow is null && Exceeds(tracks, duration, bytes))
                firstOverflow = book.AbsLibraryItemId;
        }

        var exceedsTracks = tracks > limits.MaxTracks;
        var exceedsDuration = duration > limits.MaxTotalDurationSeconds;
        var exceedsBytes = bytes > limits.MaxTotalBytes;

        var messages = new List<string>();
        if (exceedsTracks)
            messages.Add($"Track count {tracks} exceeds the limit of {limits.MaxTracks}.");
        if (exceedsDuration)
            messages.Add($"Total duration {duration / 3600:F1}h exceeds the limit of {limits.MaxTotalDurationSeconds / 3600:F1}h.");
        if (exceedsBytes)
            messages.Add($"Estimated size {bytes / 1024.0 / 1024:F0}MB exceeds the limit of {limits.MaxTotalBytes / 1024 / 1024}MB.");

        return new CapacityResult(
            new CapacityUsage(tracks, duration, bytes),
            limits,
            WithinLimits: !(exceedsTracks || exceedsDuration || exceedsBytes),
            exceedsTracks,
            exceedsDuration,
            exceedsBytes,
            firstOverflow,
            messages);
    }

    private bool Exceeds(int tracks, double duration, long bytes) =>
        tracks > limits.MaxTracks ||
        duration > limits.MaxTotalDurationSeconds ||
        bytes > limits.MaxTotalBytes;
}
