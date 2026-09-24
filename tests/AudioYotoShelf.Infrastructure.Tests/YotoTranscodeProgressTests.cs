using System.Net;
using System.Text;
using AudioYotoShelf.Core.DTOs.Yoto;
using AudioYotoShelf.Infrastructure.Services;
using AudioYotoShelf.Infrastructure.Services.Yoto;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AudioYotoShelf.Infrastructure.Tests;

/// <summary>
/// Yoto reports how far its transcode has got under <c>transcode.progress.percent</c>. Until the app
/// read it, a track's progress sat still for minutes and the transfer looked hung.
/// </summary>
public class YotoTranscodeProgressTests
{
    private const string UploadId = "upload-1";

    private static string Transcoding(int? percent) => percent is null
        ? """{"transcode":{"uploadId":"upload-1","startedAt":"2026-09-20T19:11:27Z"}}"""
        : """{"transcode":{"uploadId":"upload-1","progress":{"phase":"transcoding","percent":""" + percent + "}}}";

    private const string Finished =
        """{"transcode":{"uploadId":"upload-1","transcodedSha256":"abc123","progress":{"phase":"complete","percent":100}}}""";

    [Fact]
    public async Task Poll_ReportsYotosOwnPercentWhileItTranscodes()
    {
        var progress = new RecordingProgress();
        var sut = CreateSut(Transcoding(10), Transcoding(55), Transcoding(95), Finished);

        await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().Equal(10, 55, 95);
    }

    [Fact]
    public async Task Poll_ReportsAPercentOnlyWhenItChanges()
    {
        // Every report becomes a live update to the browser, so a repeat is noise.
        var progress = new RecordingProgress();
        var sut = CreateSut(Transcoding(40), Transcoding(40), Transcoding(40), Transcoding(60), Finished);

        await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().Equal(40, 60);
    }

    [Fact]
    public async Task Poll_BeforeYotoHasAPercent_ReportsNothingAndStillFinishes()
    {
        var progress = new RecordingProgress();
        var sut = CreateSut(Transcoding(null), Transcoding(null), Finished);

        var result = await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().BeEmpty();
        result.TranscodedSha256.Should().Be("abc123");
    }

    [Fact]
    public async Task Poll_ReturnsYotosOwnPhaseAndPercentOnTheFinishedResponse()
    {
        var sut = CreateSut(Finished);

        var result = await sut.PollTranscodeStatusAsync("token", UploadId);

        (result.Phase, result.Percent).Should().Be(("complete", 100));
    }

    [Theory]
    [InlineData("""{"transcode":{"progress":"soon"}}""")]                       // progress is not an object
    [InlineData("""{"transcode":{"progress":null}}""")]
    [InlineData("""{"transcode":{"progress":{"phase":"transcoding"}}}""")]      // no percent yet
    [InlineData("""{"transcode":{"progress":{"percent":"half"}}}""")]           // not a number
    [InlineData("""{"transcode":{"progress":{"phase":7,"percent":null}}}""")]   // wrong types
    public async Task Poll_AProgressYotoHasNotFilledInProperly_ReportsNothingAndKeepsWaiting(string body)
    {
        var progress = new RecordingProgress();
        var sut = CreateSut(body, Finished);

        var result = await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().BeEmpty();
        result.TranscodedSha256.Should().Be("abc123");
    }

    [Fact]
    public async Task Poll_ANumberWithADecimalPart_IsRoundedToAWholePercent()
    {
        var progress = new RecordingProgress();
        var sut = CreateSut("""{"transcode":{"progress":{"phase":"transcoding","percent":54.6}}}""", Finished);

        await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().Equal(55);
    }

    [Fact]
    public async Task UploadAndTranscode_WorksWithoutAProgressListener()
    {
        var sut = CreateSut(
            """{"upload":{"uploadUrl":"https://upload.example/x","uploadId":"upload-1"}}""", Transcoding(40), Finished);
        using var audio = new MemoryStream([1, 2, 3]);

        var result = await sut.UploadAndTranscodeAsync("token", audio, 3, "audio/mpeg");

        result.Sha256.Should().Be("abc123");
    }

    [Fact]
    public async Task Poll_WorksWithoutAProgressListener()
    {
        var sut = CreateSut(Transcoding(50), Finished);

        var result = await sut.PollTranscodeStatusAsync("token", UploadId);

        result.TranscodedSha256.Should().Be("abc123");
    }

    [Fact]
    public async Task Poll_ANonsensePercent_IsKeptInsideZeroToAHundred()
    {
        var progress = new RecordingProgress();
        var sut = CreateSut(Transcoding(-5), Transcoding(250), Finished);

        await sut.PollTranscodeStatusAsync("token", UploadId, progress);

        progress.Reports.Should().Equal(0, 100);
    }

    [Fact]
    public async Task UploadAndTranscode_MovesTheTracksProgressThroughTheTranscodeRatherThanParkingIt()
    {
        var progress = new RecordingProgress();
        var sut = CreateSut(
            """{"upload":{"uploadUrl":"https://upload.example/x","uploadId":"upload-1"}}""",
            Transcoding(0), Transcoding(50), Transcoding(100), Finished);
        using var audio = new MemoryStream([1, 2, 3]);

        await sut.UploadAndTranscodeAsync("token", audio, 3, "audio/mpeg", progress);

        var transcodeReports = progress.Reports.SkipWhile(p => p < YotoUploadProgress.TranscodeStart).ToList();
        transcodeReports.Should().BeInAscendingOrder();
        transcodeReports.Should().Contain(p => p > YotoUploadProgress.TranscodeStart && p < YotoUploadProgress.Complete);
        transcodeReports.Last().Should().Be(YotoUploadProgress.Complete);
    }

    [Theory]
    [InlineData(0, 60)]   // the transcode has begun: the track is at the point the upload ended
    [InlineData(50, 80)]
    [InlineData(100, 100)]
    public void YotoPercent_MapsOntoTheTranscodeShareOfTheTracksProgress(int yotoPercent, int expectedTrackProgress) =>
        YotoUploadProgress.FromTranscodePercent(yotoPercent).Should().Be(expectedTrackProgress);

    [Theory]
    [InlineData(60, 0)]
    [InlineData(80, 50)]
    [InlineData(100, 100)]
    public void TrackProgress_MapsBackToYotosPercent(int trackProgress, int expectedYotoPercent) =>
        YotoUploadProgress.ToTranscodePercent(trackProgress).Should().Be(expectedYotoPercent);

    [Theory]
    [InlineData(30, 2, 17, "Uploading track 2/17…")]
    [InlineData(60, 2, 17, "Transcoding track 2/17 on Yoto…")]
    [InlineData(80, 2, 17, "Transcoding track 2/17 on Yoto… 50%")]
    [InlineData(97, 17, 17, "Transcoding track 17/17 on Yoto… 92%")]
    public void TheStepTextShowsYotosProgressOnceItHasSome(int trackProgress, int track, int tracks, string expected) =>
        TransferOrchestrator.DescribeUploadStep(trackProgress, track, tracks).Should().Be(expected);

    // --- helpers ---

    private static TestableYotoService CreateSut(params string[] responses)
    {
        var handler = new QueuedJsonHandler(responses);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("Yoto")).Returns(() => new HttpClient(handler, disposeHandler: false));
        factory.Setup(f => f.CreateClient("YotoUpload")).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new TestableYotoService(factory.Object);
    }

    /// <summary>Reports synchronously, so a test reads them in order without racing a thread pool.</summary>
    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Reports { get; } = [];
        public void Report(int value) => Reports.Add(value);
    }

    private sealed class TestableYotoService(IHttpClientFactory factory)
        : YotoService(factory, new ConfigurationBuilder().Build(), Mock.Of<ILogger<YotoService>>())
    {
        protected override Task DelayBetweenTranscodePollsAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Answers each request in turn; the last answer repeats. The PUT upload gets an empty 200.</summary>
    private sealed class QueuedJsonHandler(string[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var body = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
