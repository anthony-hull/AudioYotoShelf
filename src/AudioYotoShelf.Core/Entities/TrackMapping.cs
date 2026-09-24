namespace AudioYotoShelf.Core.Entities;

/// <summary>
/// Maps an individual audio file or chapter from Audiobookshelf to a Yoto track.
/// Stores the Yoto SHA256 to avoid re-uploading identical content.
/// </summary>
public class TrackMapping : BaseEntity
{
    public Guid CardTransferId { get; set; }
    public CardTransfer CardTransfer { get; set; } = null!;

    // ABS source reference
    public required string AbsFileIno { get; set; }
    public required string ChapterTitle { get; set; }
    public int ChapterIndex { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public long FileSizeBytes { get; set; }

    // Yoto target reference
    public string? YotoUploadId { get; set; }
    public string? YotoTranscodedSha256 { get; set; }
    public string? YotoTrackUrl { get; set; }
    public double? TranscodedDuration { get; set; }
    public long? TranscodedFileSize { get; set; }
    /// <summary>What Yoto actually transcoded the audio to (e.g. "opus", "aac"), not what we declared
    /// on upload. The card's track <c>Format</c> must match this, or the Yoto player fails to decode
    /// the track after a couple of seconds. Null on a row from before this was tracked.</summary>
    public string? TranscodedFormat { get; set; }

    // Icon for this chapter
    public Guid? GeneratedIconId { get; set; }
    public GeneratedIcon? GeneratedIcon { get; set; }

    public bool IsUploaded => !string.IsNullOrEmpty(YotoTranscodedSha256);
}
