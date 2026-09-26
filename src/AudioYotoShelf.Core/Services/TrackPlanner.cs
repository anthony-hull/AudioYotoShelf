using AudioYotoShelf.Core.Configuration;
using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;

namespace AudioYotoShelf.Core.Services;

public class TrackPlanner(YotoCardLimits limits) : ITrackPlanner
{
    public IReadOnlyList<SourceTrack> Plan(AbsBookMedia media, TrackGrouping grouping = TrackGrouping.Chapters)
    {
        var chapters = PlanChapters(media);
        return ApplyGrouping(chapters, grouping, media.Metadata.Title ?? "Unknown");
    }

    public IReadOnlyList<SourceTrack> ApplyGrouping(
        IReadOnlyList<SourceTrack> chapterTracks, TrackGrouping grouping, string bookTitle)
    {
        if (chapterTracks.Count == 0) return chapterTracks;

        return grouping switch
        {
            TrackGrouping.SingleTrack => [Merge(chapterTracks, bookTitle)],
            TrackGrouping.Auto => FitsOneTrack(chapterTracks) ? [Merge(chapterTracks, bookTitle)] : chapterTracks,
            _ => chapterTracks
        };
    }

    private static IReadOnlyList<SourceTrack> PlanChapters(AbsBookMedia media)
    {
        var bookTitle = media.Metadata.Title ?? "Unknown";

        if (media.NumAudioFiles > 1)
        {
            // Multi-file book: each audio file is a track.
            return media.AudioFiles.OrderBy(f => f.Index).Select(f =>
            {
                var title = ChapterTitleResolver.ForFile(media.Chapters, f);
                return new SourceTrack(title, f.Duration, f.Size);
            }).ToList();
        }

        if (media.Chapters.Length > 0 && media.AudioFiles.Length == 1)
        {
            // Single-file book split by chapters: estimate per-chapter bytes proportionally.
            var file = media.AudioFiles[0];
            var totalDuration = file.Duration > 0 ? file.Duration : media.Duration;
            return media.Chapters.Select(c =>
            {
                var duration = c.End - c.Start;
                var bytes = totalDuration > 0
                    ? (long)(file.Size * (duration / totalDuration))
                    : file.Size;
                return new SourceTrack(c.Title, duration, bytes);
            }).ToList();
        }

        if (media.AudioFiles.Length == 1)
        {
            var f = media.AudioFiles[0];
            return [new SourceTrack(bookTitle, f.Duration, f.Size)];
        }

        return [];
    }

    private static SourceTrack Merge(IReadOnlyList<SourceTrack> tracks, string bookTitle) =>
        new(bookTitle,
            tracks.Sum(t => t.DurationSeconds),
            tracks.Sum(t => t.EstimatedBytes));

    private bool FitsOneTrack(IReadOnlyList<SourceTrack> tracks) =>
        tracks.Sum(t => t.DurationSeconds) <= limits.MaxTrackDurationSeconds &&
        tracks.Sum(t => t.EstimatedBytes) <= limits.MaxTrackBytes;
}
