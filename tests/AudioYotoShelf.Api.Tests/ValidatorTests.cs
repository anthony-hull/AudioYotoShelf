using AudioYotoShelf.Api.Configuration;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.DTOs.Transfer;
using AudioYotoShelf.Core.Enums;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace AudioYotoShelf.Api.Tests;

public class ValidatorTests
{
    // =========================================================================
    // AbsConnectRequestValidator
    // =========================================================================

    private readonly AbsConnectRequestValidator _absValidator = new();

    [Theory]
    [InlineData("http://myserver.com")]
    [InlineData("https://abs.example.com:8080")]
    public void AbsConnect_ValidUrl_Passes(string url) =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest(url, "user", "pass"))
            .ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("ftp://server.com")]
    [InlineData("not-a-url")]
    public void AbsConnect_InvalidUrl_Fails(string url) =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest(url, "user", "pass"))
            .ShouldHaveValidationErrorFor(x => x.BaseUrl);

    [Fact]
    public void AbsConnect_NoUrl_Passes() =>
        // The server may have its Audiobookshelf URL configured; the controller rejects a missing
        // URL when it does not.
        _absValidator.TestValidate(new AuthController.AbsConnectRequest(null, "user", "pass"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AbsConnect_EmptyUsername_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", "", "pass"))
            .ShouldHaveValidationErrorFor(x => x.Username);

    [Fact]
    public void AbsConnect_EmptyPassword_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", "user", ""))
            .ShouldHaveValidationErrorFor(x => x.Password);

    [Fact]
    public void AbsConnect_ApiKeyOnly_Passes() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", ApiKey: "key"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AbsConnect_ApiKeyAndPassword_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", "user", "pass", "key"))
            .ShouldHaveValidationErrorFor(x => x.ApiKey);

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public void AbsConnect_BlankApiKey_Fails(string apiKey) =>
        // Blank is not a credential: without this the app sends "Bearer    " to ABS and turns its
        // refusal into a 502, where the caller should have got a 400.
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", ApiKey: apiKey))
            .ShouldHaveValidationErrors();

    [Fact]
    public void AbsConnect_OverlongApiKey_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", ApiKey: new string('k', 4097)))
            .ShouldHaveValidationErrorFor(x => x.ApiKey);

    [Fact]
    public void AbsConnect_NoCredentials_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com"))
            .ShouldHaveValidationErrors();

    // =========================================================================
    // CreateTransferRequestValidator
    // =========================================================================

    private readonly CreateTransferRequestValidator _transferValidator = new();

    [Fact]
    public void Transfer_ValidRequest_Passes() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Transfer_EmptyItemId_Fails() =>
        _transferValidator.TestValidate(new CreateTransferRequest(""))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryItemId);

    [Fact]
    public void Transfer_ValidAgeOverrides_Passes() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMinAge: 3, OverrideMaxAge: 8))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Transfer_MinOverMax_Fails() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMinAge: 10, OverrideMaxAge: 5))
            .ShouldHaveValidationErrors();

    [Fact]
    public void Transfer_AgeOutOfRange_Fails() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMinAge: 25))
            .ShouldHaveValidationErrorFor(x => x.OverrideMinAge);

    [Fact]
    public void Transfer_NullAges_Passes() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMinAge: null, OverrideMaxAge: null))
            .ShouldNotHaveAnyValidationErrors();

    // =========================================================================
    // CreateSeriesTransferRequestValidator
    // =========================================================================

    private readonly CreateSeriesTransferRequestValidator _seriesValidator = new();

    [Fact]
    public void Series_ValidRequest_Passes() =>
        _seriesValidator.TestValidate(new CreateSeriesTransferRequest("ser-1", "lib-1"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Series_EmptySeriesId_Fails() =>
        _seriesValidator.TestValidate(new CreateSeriesTransferRequest("", "lib-1"))
            .ShouldHaveValidationErrorFor(x => x.AbsSeriesId);

    [Fact]
    public void Series_EmptyLibraryId_Fails() =>
        _seriesValidator.TestValidate(new CreateSeriesTransferRequest("ser-1", ""))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryId);

    // =========================================================================
    // BatchTransferRequestValidator (Phase 2)
    // =========================================================================

    private readonly BatchTransferRequestValidator _batchValidator = new();

    [Fact]
    public void Batch_ValidRequest_Passes() =>
        _batchValidator.TestValidate(new BatchTransferRequest(["item-1", "item-2"]))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Batch_EmptyArray_Fails() =>
        _batchValidator.TestValidate(new BatchTransferRequest([]))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryItemIds);

    [Fact]
    public void Batch_Over50_Fails()
    {
        var ids = Enumerable.Range(0, 51).Select(i => $"item-{i}").ToArray();
        _batchValidator.TestValidate(new BatchTransferRequest(ids))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryItemIds);
    }

    [Fact]
    public void Batch_EmptyItemInArray_Fails() =>
        _batchValidator.TestValidate(new BatchTransferRequest(["item-1", "", "item-3"]))
            .ShouldHaveValidationErrors();

    [Fact]
    public void Batch_MinOverMax_Fails() =>
        _batchValidator.TestValidate(new BatchTransferRequest(["item-1"], OverrideMinAge: 10, OverrideMaxAge: 5))
            .ShouldHaveValidationErrors();

    [Fact]
    public void Batch_Exactly50_Passes()
    {
        var ids = Enumerable.Range(0, 50).Select(i => $"item-{i}").ToArray();
        _batchValidator.TestValidate(new BatchTransferRequest(ids))
            .ShouldNotHaveAnyValidationErrors();
    }

    // =========================================================================
    // UpdateSettingsRequestValidator (Phase 3)
    // =========================================================================

    private readonly UpdateSettingsRequestValidator _settingsValidator = new();

    [Fact]
    public void Settings_ValidRange_Passes() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultMinAge: 3, DefaultMaxAge: 10))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Settings_MinOverMax_Fails() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultMinAge: 10, DefaultMaxAge: 5))
            .ShouldHaveValidationErrors();

    [Fact]
    public void Settings_OutOfRange_Fails() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultMinAge: 25))
            .ShouldHaveValidationErrorFor(x => x.DefaultMinAge);

    [Fact]
    public void Settings_AllNull_Passes() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest())
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Settings_OnlyLibraryId_Passes() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultLibraryId: "lib-1"))
            .ShouldNotHaveAnyValidationErrors();

    // =========================================================================
    // Mutation-testing additions — every age bound, the min < max rule, and the playlist validators
    // =========================================================================

    /// <summary>(min, max, is valid): 0-18 inclusive, either may be absent, and min must be below max.</summary>
    public static TheoryData<int?, int?, bool> AgeRangeCases => new()
    {
        { null, null, true },
        { 3, null, true },          // a lone min is fine: nothing to compare it with
        { null, 8, true },          // a lone max is fine
        { 0, 1, true },
        { 17, 18, true },
        { 0, null, true },
        { 18, null, true },
        { null, 0, true },
        { null, 18, true },
        { 5, 5, false },            // equal is not "less than"
        { 6, 5, false },
        { -1, null, false },
        { 19, null, false },
        { null, -1, false },
        { null, 19, false },
    };

    /// <summary>The same bounds without the min &lt; max rule, for the validator that does not have one.</summary>
    public static TheoryData<int?, int?, bool> AgeBoundCases => new()
    {
        { 0, null, true },
        { 18, null, true },
        { null, 0, true },
        { null, 18, true },
        { -1, null, false },
        { 19, null, false },
        { null, -1, false },
        { null, 19, false },
    };

    [Theory]
    [MemberData(nameof(AgeRangeCases))]
    public void Transfer_AgeRange_IsValidOnlyWhenInBoundsAndMinBelowMax(int? min, int? max, bool isValid) =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMinAge: min, OverrideMaxAge: max))
            .IsValid.Should().Be(isValid);

    [Theory]
    [MemberData(nameof(AgeRangeCases))]
    public void Batch_AgeRange_IsValidOnlyWhenInBoundsAndMinBelowMax(int? min, int? max, bool isValid) =>
        _batchValidator.TestValidate(new BatchTransferRequest(["item-1"], OverrideMinAge: min, OverrideMaxAge: max))
            .IsValid.Should().Be(isValid);

    [Theory]
    [MemberData(nameof(AgeRangeCases))]
    public void Settings_AgeRange_IsValidOnlyWhenInBoundsAndMinBelowMax(int? min, int? max, bool isValid) =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultMinAge: min, DefaultMaxAge: max))
            .IsValid.Should().Be(isValid);

    [Theory]
    [MemberData(nameof(AgeBoundCases))]
    public void Series_AgeBounds_AreZeroToEighteen(int? min, int? max, bool isValid) =>
        _seriesValidator.TestValidate(new CreateSeriesTransferRequest("ser-1", "lib-1", OverrideMinAge: min, OverrideMaxAge: max))
            .IsValid.Should().Be(isValid);

    [Fact]
    public void Transfer_OutOfRangeMax_IsReportedAgainstMax() =>
        _transferValidator.TestValidate(new CreateTransferRequest("item-1", OverrideMaxAge: 25))
            .ShouldHaveValidationErrorFor(x => x.OverrideMaxAge);

    [Fact]
    public void Series_OutOfRangeMax_IsReportedAgainstMax() =>
        _seriesValidator.TestValidate(new CreateSeriesTransferRequest("ser-1", "lib-1", OverrideMaxAge: 25))
            .ShouldHaveValidationErrorFor(x => x.OverrideMaxAge);

    [Fact]
    public void Batch_OutOfRangeMax_IsReportedAgainstMax() =>
        _batchValidator.TestValidate(new BatchTransferRequest(["item-1"], OverrideMaxAge: 25))
            .ShouldHaveValidationErrorFor(x => x.OverrideMaxAge);

    [Fact]
    public void Settings_OutOfRangeMax_IsReportedAgainstMax() =>
        _settingsValidator.TestValidate(new UpdateSettingsRequest(DefaultMaxAge: 25))
            .ShouldHaveValidationErrorFor(x => x.DefaultMaxAge);

    // ---- AbsConnect: an API key excludes any username or password ----

    [Theory]
    [InlineData("user", null)]
    [InlineData(null, "pass")]
    [InlineData("user", "pass")]
    public void AbsConnect_ApiKeyWithAnyPasswordCredential_Fails(string? username, string? password) =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", username, password, "key"))
            .ShouldHaveValidationErrorFor(x => x.ApiKey);

    [Fact]
    public void AbsConnect_ApiKeyWithBlankPasswordCredentials_Passes() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", "", "", "key"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AbsConnect_UsernameOver256Characters_Fails() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", new string('u', 257), "pass"))
            .ShouldHaveValidationErrorFor(x => x.Username);

    [Fact]
    public void AbsConnect_UsernameOf256Characters_Passes() =>
        _absValidator.TestValidate(new AuthController.AbsConnectRequest("http://x.com", new string('u', 256), "pass"))
            .ShouldNotHaveAnyValidationErrors();

    // =========================================================================
    // CreatePlaylistRequestValidator
    // =========================================================================

    private readonly CreatePlaylistRequestValidator _createPlaylistValidator = new();

    [Fact]
    public void CreatePlaylist_ValidRequest_Passes() =>
        _createPlaylistValidator.TestValidate(new CreatePlaylistRequest("Bedtime"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void CreatePlaylist_EmptyName_Fails() =>
        _createPlaylistValidator.TestValidate(new CreatePlaylistRequest(""))
            .ShouldHaveValidationErrorFor(x => x.Name);

    [Fact]
    public void CreatePlaylist_NameOf256Characters_Passes() =>
        _createPlaylistValidator.TestValidate(new CreatePlaylistRequest(new string('n', 256)))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void CreatePlaylist_NameOf257Characters_Fails() =>
        _createPlaylistValidator.TestValidate(new CreatePlaylistRequest(new string('n', 257)))
            .ShouldHaveValidationErrorFor(x => x.Name);

    [Fact]
    public void CreatePlaylist_UnknownGrouping_Fails() =>
        _createPlaylistValidator.TestValidate(new CreatePlaylistRequest("Bedtime", (TrackGrouping)99))
            .ShouldHaveValidationErrorFor(x => x.DefaultGrouping);

    // =========================================================================
    // AddPlaylistItemsRequestValidator
    // =========================================================================

    private readonly AddPlaylistItemsRequestValidator _addItemsValidator = new();

    private static string[] ItemIds(int count) => Enumerable.Range(0, count).Select(i => $"item-{i}").ToArray();

    [Fact]
    public void AddPlaylistItems_ValidRequest_Passes() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest(["item-1"]))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AddPlaylistItems_None_Fails() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest([]))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryItemIds);

    [Fact]
    public void AddPlaylistItems_Exactly100_Passes() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest(ItemIds(100)))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AddPlaylistItems_101_Fails() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest(ItemIds(101)))
            .ShouldHaveValidationErrorFor(x => x.AbsLibraryItemIds);

    [Fact]
    public void AddPlaylistItems_EmptyId_Fails() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest(["item-1", ""]))
            .ShouldHaveValidationErrors();

    [Fact]
    public void AddPlaylistItems_IdOf257Characters_Fails() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest([new string('i', 257)]))
            .ShouldHaveValidationErrors();

    [Fact]
    public void AddPlaylistItems_IdOf256Characters_Passes() =>
        _addItemsValidator.TestValidate(new AddPlaylistItemsRequest([new string('i', 256)]))
            .ShouldNotHaveAnyValidationErrors();

    // =========================================================================
    // AddPlaylistSeriesRequestValidator / ReorderPlaylistRequestValidator
    // =========================================================================

    private readonly AddPlaylistSeriesRequestValidator _addSeriesValidator = new();
    private readonly ReorderPlaylistRequestValidator _reorderValidator = new();

    [Fact]
    public void AddPlaylistSeries_ValidRequest_Passes() =>
        _addSeriesValidator.TestValidate(new AddPlaylistSeriesRequest("ser-1"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void AddPlaylistSeries_EmptyId_Fails() =>
        _addSeriesValidator.TestValidate(new AddPlaylistSeriesRequest(""))
            .ShouldHaveValidationErrorFor(x => x.AbsSeriesId);

    [Fact]
    public void AddPlaylistSeries_IdOf257Characters_Fails() =>
        _addSeriesValidator.TestValidate(new AddPlaylistSeriesRequest(new string('s', 257)))
            .ShouldHaveValidationErrorFor(x => x.AbsSeriesId);

    [Fact]
    public void ReorderPlaylist_WithItems_Passes() =>
        _reorderValidator.TestValidate(new ReorderPlaylistRequest([Guid.NewGuid()]))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ReorderPlaylist_NoItems_Fails() =>
        _reorderValidator.TestValidate(new ReorderPlaylistRequest([]))
            .ShouldHaveValidationErrorFor(x => x.OrderedItemIds);
}
