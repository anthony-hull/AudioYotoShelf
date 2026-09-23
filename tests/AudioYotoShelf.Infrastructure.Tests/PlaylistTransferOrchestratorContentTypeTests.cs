using AudioYotoShelf.Infrastructure.Services;
using FluentAssertions;

namespace AudioYotoShelf.Infrastructure.Tests;

// Yoto's transcoder trusts the declared content type rather than sniffing the bytes, so a file that
// reached the playlist upload without being merged or chapter-split still carries the extension
// Audiobookshelf served it with. Falling into the "everything else is mp3" bucket is what made a real
// ogg/opus book play about a second per track before skipping to the next — see TransferOrchestrator's
// equivalent bug in TransferOrchestratorPipelineTests.
public class PlaylistTransferOrchestratorContentTypeTests
{
    [Theory]
    [InlineData("book.m4a", "audio/mp4")]
    [InlineData("book.m4b", "audio/mp4")]
    [InlineData("book.mp4", "audio/mp4")]
    [InlineData("book.aac", "audio/mp4")]
    [InlineData("BOOK.M4A", "audio/mp4")]
    [InlineData("book.ogg", "audio/ogg")]
    [InlineData("book.opus", "audio/ogg")]
    [InlineData("book.flac", "audio/flac")]
    [InlineData("book.wav", "audio/wav")]
    [InlineData("book.mp3", "audio/mpeg")]
    [InlineData("book", "audio/mpeg")]
    public void ContentTypeFor_MapsTheExtensionToTheTypeYotoIsToldItIs(string fileName, string expectedContentType) =>
        PlaylistTransferOrchestrator.ContentTypeFor(fileName).Should().Be(expectedContentType);
}
