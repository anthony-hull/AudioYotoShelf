using AudioYotoShelf.Api.Configuration;
using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Transfer;
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
}
