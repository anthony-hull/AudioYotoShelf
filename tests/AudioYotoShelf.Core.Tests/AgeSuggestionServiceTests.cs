using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Services;
using AudioYotoShelf.Core.Tests.Helpers;
using FluentAssertions;

namespace AudioYotoShelf.Core.Tests;

public class AgeSuggestionServiceTests
{
    private readonly AgeSuggestionService _sut = new();

    // =========================================================================
    // Genre-based inference
    // =========================================================================

    [Theory]
    [InlineData(new string[] { "Children's Fiction" }, 2, 8)]
    [InlineData(new string[] { "Fairy Tales" }, 2, 8)]
    [InlineData(new string[] { "Bedtime Stories" }, 2, 8)]
    public void SuggestAgeRange_ChildrenGenres_ReturnsYoungRange(string[] genres, int expectedMinLow, int expectedMaxHigh)
    {
        var metadata = TestData.CreateAbsMetadata(genres: genres);
        var result = _sut.SuggestAgeRange(metadata, 3600, 10);

        result.SuggestedMinAge.Should().BeInRange(expectedMinLow, 6);
        result.SuggestedMaxAge.Should().BeInRange(5, expectedMaxHigh);
        result.Source.Should().Be(AgeRangeSource.GenreInferred);
    }

    [Theory]
    [InlineData((object)new string[] { "Middle Grade" })]
    [InlineData((object)new string[] { "Chapter Book" })]
    public void SuggestAgeRange_MiddleGradeGenres_ReturnsMidRange(string[] genres)
    {
        var metadata = TestData.CreateAbsMetadata(genres: genres);
        var result = _sut.SuggestAgeRange(metadata, 18000, 15);

        result.SuggestedMinAge.Should().BeInRange(4, 10);
        result.SuggestedMaxAge.Should().BeInRange(8, 14);
    }

    [Theory]
    [InlineData((object)new string[] { "Young Adult" })]
    [InlineData((object)new string[] { "YA Fiction" })]
    [InlineData((object)new string[] { "Coming of Age" })]
    public void SuggestAgeRange_YoungAdultGenres_ReturnsTeenRange(string[] genres)
    {
        var metadata = TestData.CreateAbsMetadata(genres: genres);
        var result = _sut.SuggestAgeRange(metadata, 36000, 25);

        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(6);
        result.SuggestedMaxAge.Should().BeGreaterOrEqualTo(10);
    }

    [Theory]
    [InlineData((object)new string[] { "Thriller" })]
    [InlineData((object)new string[] { "Horror" })]
    [InlineData((object)new string[] { "Crime Fiction" })]
    public void SuggestAgeRange_AdultGenres_ReturnsOlderRange(string[] genres)
    {
        var metadata = TestData.CreateAbsMetadata(genres: genres);
        var result = _sut.SuggestAgeRange(metadata, 36000, 30);

        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(6);
    }

    // =========================================================================
    // Keyword-based inference
    // =========================================================================

    [Theory]
    [InlineData("A story about a princess and her magical unicorn", "princess")]
    [InlineData("Adventures of a brave dragon slayer wizard", "dragon")]
    public void SuggestAgeRange_DescriptionKeywords_DetectsSignals(string description, string expectedKeyword)
    {
        var metadata = TestData.CreateAbsMetadata(genres: [], description: description);
        var result = _sut.SuggestAgeRange(metadata, 3600, 10);

        result.Signals.Should().Contain(s => s.Signal == "Keyword");
        result.Signals.Should().Contain(s =>
            s.Value.Contains(expectedKeyword, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestAgeRange_WarKeywords_PushesAgeUp()
    {
        var metadata = TestData.CreateAbsMetadata(
            genres: [],
            description: "A harrowing tale of war and death in the trenches");
        var result = _sut.SuggestAgeRange(metadata, 36000, 30);

        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(6);
    }

    // =========================================================================
    // Duration-based inference
    // =========================================================================

    [Theory]
    [InlineData(600, 2, 5)]      // 10 minutes → very young
    [InlineData(5400, 4, 8)]     // 90 minutes → young children
    [InlineData(14400, 6, 12)]   // 4 hours → middle grade
    [InlineData(43200, 8, 18)]   // 12 hours → older
    public void SuggestAgeRange_DurationHeuristic_AdjustsRange(
        double durationSeconds, int expectedMinLow, int expectedMaxHigh)
    {
        var metadata = TestData.CreateAbsMetadata(genres: []);
        var result = _sut.SuggestAgeRange(metadata, durationSeconds, 10);

        result.Signals.Should().Contain(s => s.Signal == "Duration");
        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(expectedMinLow - 2);
        result.SuggestedMaxAge.Should().BeLessThanOrEqualTo(expectedMaxHigh + 2);
    }

    // =========================================================================
    // Explicit content flag
    // =========================================================================

    [Fact]
    public void SuggestAgeRange_ExplicitContent_EnforcesHighMinAge()
    {
        var metadata = TestData.CreateAbsMetadata(isExplicit: true);
        var result = _sut.SuggestAgeRange(metadata, 28800, 30);

        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(8);
        result.Signals.Should().Contain(s => s.Signal == "ExplicitContent" && s.Weight >= 90);
    }

    [Fact]
    public void SuggestAgeRange_ExplicitOverridesChildrenGenre_CompromisesRange()
    {
        var metadata = TestData.CreateAbsMetadata(
            genres: ["Children's Fiction"], isExplicit: true);
        var result = _sut.SuggestAgeRange(metadata, 3600, 10);

        // Explicit flag should pull the weighted average up significantly
        result.SuggestedMinAge.Should().BeGreaterOrEqualTo(4);
    }

    // =========================================================================
    // Default fallback
    // =========================================================================

    [Fact]
    public void SuggestAgeRange_NoGenreOrKeywordSignals_FallsBackToTheDurationRange()
    {
        var metadata = TestData.CreateAbsMetadata(genres: [], description: "");
        var result = _sut.SuggestAgeRange(metadata, 7200, 10);

        // Duration is always a signal, so "no metadata" still yields the 2 h bucket (6-12), never a fixed default.
        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((6, 12));
        result.Source.Should().Be(AgeRangeSource.DurationInferred);
    }

    // =========================================================================
    // Edge cases and invariants
    // =========================================================================

    [Fact]
    public void SuggestAgeRange_Always_MinIsLessThanMax()
    {
        var testCases = new[]
        {
            TestData.CreateAbsMetadata(genres: ["Children"], isExplicit: true),
            TestData.CreateAbsMetadata(genres: ["Horror", "Children"]),
            TestData.CreateAbsMetadata(genres: [], description: "war princess baby"),
        };

        foreach (var metadata in testCases)
        {
            var result = _sut.SuggestAgeRange(metadata, 3600, 10);
            result.SuggestedMaxAge.Should().BeGreaterThan(result.SuggestedMinAge,
                $"Failed for genres: {string.Join(",", metadata.Genres ?? [])}");
        }
    }

    [Fact]
    public void SuggestAgeRange_Always_ClampsTo0Through18()
    {
        var extremeCases = new[] { 10, 100, 1000, 100000 };

        foreach (var duration in extremeCases)
        {
            var result = _sut.SuggestAgeRange(
                TestData.CreateAbsMetadata(genres: ["Horror", "Thriller"], isExplicit: true),
                duration, 1);

            result.SuggestedMinAge.Should().BeInRange(0, 18);
            result.SuggestedMaxAge.Should().BeInRange(1, 18);
        }
    }

    [Fact]
    public void SuggestAgeRange_MultipleGenres_CombinesSignals()
    {
        var metadata = TestData.CreateAbsMetadata(
            genres: ["Science Fiction", "Young Adult"]);
        var result = _sut.SuggestAgeRange(metadata, 28800, 20);

        result.Signals.Should().HaveCountGreaterOrEqualTo(2,
            "Multiple matching genres should produce multiple signals");
    }

    [Fact]
    public void SuggestAgeRange_ReasonText_IsNotEmpty()
    {
        var metadata = TestData.CreateAbsMetadata();
        var result = _sut.SuggestAgeRange(metadata, 3600, 10);

        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SuggestAgeRange_NullGenres_DoesNotThrow()
    {
        var metadata = TestData.CreateAbsMetadata(genres: null);
        var act = () => _sut.SuggestAgeRange(metadata, 3600, 10);

        act.Should().NotThrow();
    }

    [Fact]
    public void SuggestAgeRange_NullDescription_DoesNotThrow()
    {
        var metadata = TestData.CreateAbsMetadata(description: null);
        var act = () => _sut.SuggestAgeRange(metadata, 3600, 10);

        act.Should().NotThrow();
    }

    // =========================================================================
    // Mutation-testing additions — exact values, so a swapped bound or a dropped signal is caught
    // =========================================================================

    private static AbsBookMetadata Plain(string[]? genres = null, string description = "", bool isExplicit = false) =>
        TestData.CreateAbsMetadata(genres: genres ?? [], description: description, isExplicit: isExplicit);

    [Theory]
    [InlineData(29, 2, 5)]
    [InlineData(30, 4, 8)]      // exactly 30 minutes moves up a bucket
    [InlineData(119, 4, 8)]
    [InlineData(120, 6, 12)]    // exactly 2 hours moves up a bucket
    [InlineData(479, 6, 12)]
    [InlineData(480, 8, 18)]    // exactly 8 hours moves up a bucket
    public void SuggestAgeRange_DurationOnly_PicksTheBucketForThatLength(int minutes, int expectedMin, int expectedMax)
    {
        var result = _sut.SuggestAgeRange(Plain(), minutes * 60.0, 1);

        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((expectedMin, expectedMax));
    }

    [Fact]
    public void SuggestAgeRange_DurationOnly_ReportsDurationAsTheReason()
    {
        var result = _sut.SuggestAgeRange(Plain(), 30 * 60.0, 1);

        result.Source.Should().Be(AgeRangeSource.DurationInferred);
        result.Reason.Should().Be("Based on duration: 30 minutes");
        result.Signals.Should().Equal(new AgeSuggestionDetail("Duration", "30 minutes", 30));
    }

    [Fact]
    public void SuggestAgeRange_PictureBookGenre_BlendsGenreAndDurationExactly()
    {
        // Picture book (2-5, weight 90) + 10 min (2-5, weight 40): every bound agrees, so 2-5.
        var result = _sut.SuggestAgeRange(Plain(["Picture Book"]), 10 * 60.0, 1);

        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((2, 5));
        result.Source.Should().Be(AgeRangeSource.GenreInferred);
        result.Reason.Should().Be("Based on genre: picture book");
    }

    [Fact]
    public void SuggestAgeRange_GenreAndDurationDisagree_WeightsEachBoundSeparately()
    {
        // Thriller (12-18, weight 70) + 10 min (2-5, weight 40):
        // min = (12*70 + 2*40) / 110 = 8.36 -> 8 ; max = (18*70 + 5*40) / 110 = 13.3 -> 13.
        var result = _sut.SuggestAgeRange(Plain(["Thriller"]), 10 * 60.0, 1);

        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((8, 13));
    }

    [Fact]
    public void SuggestAgeRange_DescriptionKeyword_BlendsKeywordAndDurationExactly()
    {
        // Princess (4-8, weight 60) + 10 min (2-5, weight 40):
        // min = (4*60 + 2*40) / 100 = 3.2 -> 3 ; max = (8*60 + 5*40) / 100 = 6.8 -> 7.
        var result = _sut.SuggestAgeRange(Plain(description: "A princess in a castle"), 10 * 60.0, 1);

        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((3, 7));
        result.Source.Should().Be(AgeRangeSource.KeywordInferred);
        result.Reason.Should().Be("Based on keyword: princess");
    }

    [Fact]
    public void SuggestAgeRange_SeveralKeywordsInOneRule_ReportsTheFirstListed()
    {
        var result = _sut.SuggestAgeRange(Plain(description: "a wizard and a dragon"), 10 * 60.0, 1);

        result.Reason.Should().Be("Based on keyword: dragon");
    }

    [Fact]
    public void SuggestAgeRange_ExplicitFlag_BlendsExplicitAndDurationExactly()
    {
        // Explicit (14-18, weight 95) + 10 min (2-5, weight 40):
        // min = (14*95 + 2*40) / 135 = 10.4 -> 10 ; max = (18*95 + 5*40) / 135 = 14.1 -> 14.
        var result = _sut.SuggestAgeRange(Plain(isExplicit: true), 10 * 60.0, 1);

        (result.SuggestedMinAge, result.SuggestedMaxAge).Should().Be((10, 14));
        result.Signals.Should().Contain(new AgeSuggestionDetail("ExplicitContent", "true", 95));
    }

    [Fact]
    public void SuggestAgeRange_GenresAndDescriptionThatMatchNothing_AddNoSignalsBeyondDuration()
    {
        var result = _sut.SuggestAgeRange(Plain(["Cookbook"], "Recipes for every day"), 10 * 60.0, 1);

        result.Signals.Select(s => s.Signal).Should().Equal("Duration");
    }

    [Fact]
    public void SuggestAgeRange_NullGenresAndDescription_FallBackToDurationOnly()
    {
        var metadata = TestData.CreateAbsMetadata() with { Genres = null, Description = null };

        var result = _sut.SuggestAgeRange(metadata, 10 * 60.0, 1);

        result.Signals.Select(s => s.Signal).Should().Equal("Duration");
    }
}
