using AudioYotoShelf.Core.DTOs.Audiobookshelf;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using AudioYotoShelf.Core.Interfaces;

namespace AudioYotoShelf.Core.Services;

public class AgeSuggestionService : IAgeSuggestionService
{
    private static readonly Dictionary<string[], (int Min, int Max, int Weight)> GenreRules = new()
    {
        { ["picture book", "nursery", "toddler", "baby", "board book"], (2, 5, 90) },
        { ["children", "fairy tale", "bedtime", "fable", "storybook"], (3, 7, 80) },
        { ["middle grade", "chapter book", "early reader"], (6, 10, 85) },
        { ["young adult", "ya ", "teen", "coming of age"], (10, 14, 85) },
        { ["thriller", "horror", "crime", "mystery"], (12, 18, 70) },
        { ["romance", "adult fiction"], (14, 18, 70) },
        { ["science fiction", "fantasy", "adventure"], (8, 14, 50) },
        { ["educational", "learning", "school"], (5, 12, 60) },
        { ["nonfiction", "biography", "history"], (10, 18, 40) },
    };

    private static readonly Dictionary<string[], (int Min, int Max, int Weight)> KeywordRules = new()
    {
        { ["princess", "dragon", "magic", "wizard", "unicorn"], (4, 8, 60) },
        { ["school", "homework", "teacher", "friends", "bully"], (6, 12, 50) },
        { ["war", "death", "violence", "blood"], (12, 18, 75) },
        { ["love", "kiss", "relationship", "dating"], (12, 18, 65) },
        { ["puppy", "kitten", "farm", "animal"], (3, 7, 55) },
    };

    public AgeSuggestionResponse SuggestAgeRange(AbsBookMetadata metadata, double durationSeconds, int chapterCount)
    {
        var signals = new List<AgeSuggestionDetail>();
        var weightedMinAges = new List<(int Age, int Weight)>();
        var weightedMaxAges = new List<(int Age, int Weight)>();

        // Signal 1: Genre matching
        var genres = (metadata.Genres ?? [])
            .Select(g => g.ToLowerInvariant())
            .ToArray();

        foreach (var (keywords, range) in GenreRules)
        {
            var matchedKeyword = keywords.FirstOrDefault(k => genres.Any(g => g.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (matchedKeyword is not null)
            {
                signals.Add(new AgeSuggestionDetail("Genre", matchedKeyword, range.Weight));
                weightedMinAges.Add((range.Min, range.Weight));
                weightedMaxAges.Add((range.Max, range.Weight));
            }
        }

        // Signal 2: Description keyword matching
        // Stryker disable once String : the fallback only has to contain no keyword, so any keyword-free text is equivalent
        var description = (metadata.Description ?? "").ToLowerInvariant();
        foreach (var (keywords, range) in KeywordRules)
        {
            var matchedKeyword = keywords.FirstOrDefault(k => description.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (matchedKeyword is not null)
            {
                signals.Add(new AgeSuggestionDetail("Keyword", matchedKeyword, range.Weight));
                weightedMinAges.Add((range.Min, range.Weight));
                weightedMaxAges.Add((range.Max, range.Weight));
            }
        }

        // Signal 3: Duration heuristic
        var durationMinutes = durationSeconds / 60.0;
        var (durMin, durMax, durWeight) = durationMinutes switch
        {
            < 30 => (2, 5, 40),
            < 120 => (4, 8, 30),
            < 480 => (6, 12, 25),
            _ => (8, 18, 20),
        };
        signals.Add(new AgeSuggestionDetail("Duration", $"{durationMinutes:F0} minutes", durWeight));
        weightedMinAges.Add((durMin, durWeight));
        weightedMaxAges.Add((durMax, durWeight));

        // Signal 4: Explicit content flag
        if (metadata.Explicit)
        {
            signals.Add(new AgeSuggestionDetail("ExplicitContent", "true", 95));
            weightedMinAges.Add((14, 95));
            weightedMaxAges.Add((18, 95));
        }

        // Calculate weighted averages
        // The duration signal above is unconditional, so there is always at least one signal to average.
        var totalWeight = weightedMinAges.Sum(x => x.Weight);
        var suggestedMin = (int)Math.Round(weightedMinAges.Sum(x => x.Age * x.Weight) / (double)totalWeight);
        var suggestedMax = (int)Math.Round(weightedMaxAges.Sum(x => x.Age * x.Weight) / (double)totalWeight);

        // Stryker disable once Linq : signals always holds the duration signal, so First() cannot be empty
        var topSignal = signals.OrderByDescending(s => s.Weight).First();
        var source = topSignal.Signal switch
        {
            "Genre" => AgeRangeSource.GenreInferred,
            "Keyword" => AgeRangeSource.KeywordInferred,
            "Duration" => AgeRangeSource.DurationInferred,
            _ => AgeRangeSource.Default
        };
        var reason = $"Based on {topSignal.Signal.ToLowerInvariant()}: {topSignal.Value}";

        // Ensure min < max and clamp to reasonable bounds
        suggestedMin = Math.Clamp(suggestedMin, 0, 18);
        // Stryker disable once Arithmetic : defensive guard — every rule is at least 3 years wide, so the lower bound never binds
        suggestedMax = Math.Clamp(suggestedMax, suggestedMin + 1, 18);

        return new AgeSuggestionResponse(suggestedMin, suggestedMax, reason, source, [.. signals]);
    }
}
