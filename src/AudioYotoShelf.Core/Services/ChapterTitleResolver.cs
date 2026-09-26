using AudioYotoShelf.Core.DTOs.Audiobookshelf;

namespace AudioYotoShelf.Core.Services;

/// <summary>Maps one file of a multi-file Audiobookshelf audiobook to its chapter title.</summary>
public static class ChapterTitleResolver
{
    /// <summary>
    /// Audiobookshelf's <see cref="AbsAudioFile.Index"/> is 1-based (confirmed live: the first file
    /// of a book reports Index 1, not 0), but <see cref="AbsBookMedia.Chapters"/> is a plain 0-based
    /// array in file order. Falls back to the file's own name when there's no corresponding chapter
    /// (fewer chapters than files, or Audiobookshelf hasn't populated chapters for this item).
    /// </summary>
    public static string ForFile(IReadOnlyList<AbsChapter> chapters, AbsAudioFile file)
    {
        var chapterPosition = file.Index - 1;
        return chapterPosition >= 0 && chapterPosition < chapters.Count
            ? chapters[chapterPosition].Title
            : file.Metadata.Filename;
    }
}
