namespace AudioYotoShelf.Core.DTOs.Yoto;

/// <summary>
/// How one track's upload-and-transcode is reported, as 0-100 for that track. The upload is quick
/// and gets the first share; Yoto's transcode, which is where the minutes go, gets the rest so the
/// bar can follow the percentage Yoto itself reports.
/// </summary>
public static class YotoUploadProgress
{
    /// <summary>The upload has finished and Yoto has begun transcoding.</summary>
    public const int TranscodeStart = 60;

    public const int Complete = 100;

    private const int PercentScale = 100;

    /// <summary>Places Yoto's own 0-100 transcode percentage within this track's share of the bar.</summary>
    public static int FromTranscodePercent(int yotoPercent) =>
        TranscodeStart + Math.Clamp(yotoPercent, 0, PercentScale) * (Complete - TranscodeStart) / PercentScale;

    /// <summary>The inverse of <see cref="FromTranscodePercent"/>: Yoto's percentage for a track's progress.</summary>
    public static int ToTranscodePercent(int trackProgress) =>
        (Math.Clamp(trackProgress, TranscodeStart, Complete) - TranscodeStart) * PercentScale / (Complete - TranscodeStart);
}
