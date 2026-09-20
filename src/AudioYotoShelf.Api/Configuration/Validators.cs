using AudioYotoShelf.Api.Controllers;
using AudioYotoShelf.Core.DTOs.Playlist;
using AudioYotoShelf.Core.DTOs.Transfer;
using FluentValidation;

namespace AudioYotoShelf.Api.Configuration;

public class AbsConnectRequestValidator : AbstractValidator<AuthController.AbsConnectRequest>
{
    /// <summary>Matches the AudiobookshelfToken column the key is stored in.</summary>
    private const int MaxApiKeyLength = 4096;

    public AbsConnectRequestValidator()
    {
        // Optional: the server may have its Audiobookshelf URL configured (AuthController resolves it).
        RuleFor(x => x.BaseUrl)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == "http" || uri.Scheme == "https"))
            .When(x => !string.IsNullOrEmpty(x.BaseUrl))
            .WithMessage("Must be a valid HTTP/HTTPS URL");

        // Blank is not a credential — whitespace here would otherwise reach ABS as "Bearer   ".
        RuleFor(x => x.ApiKey).MaximumLength(MaxApiKeyLength);

        When(x => string.IsNullOrWhiteSpace(x.ApiKey), () =>
        {
            RuleFor(x => x.Username).NotEmpty().MaximumLength(256);
            RuleFor(x => x.Password).NotEmpty();
        }).Otherwise(() =>
        {
            RuleFor(x => x.ApiKey)
                .Must((request, _) => string.IsNullOrEmpty(request.Username) && string.IsNullOrEmpty(request.Password))
                .WithMessage("Use either an API key or a username and password, not both");
        });
    }
}

public class CreateTransferRequestValidator : AbstractValidator<CreateTransferRequest>
{
    public CreateTransferRequestValidator()
    {
        RuleFor(x => x.AbsLibraryItemId).NotEmpty().MaximumLength(256);

        RuleFor(x => x.OverrideMinAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMinAge.HasValue);

        RuleFor(x => x.OverrideMaxAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMaxAge.HasValue);

        RuleFor(x => x)
            .Must(x => !x.OverrideMinAge.HasValue || !x.OverrideMaxAge.HasValue ||
                       x.OverrideMinAge < x.OverrideMaxAge)
            .WithMessage("Min age must be less than max age");
    }
}

public class CreateSeriesTransferRequestValidator : AbstractValidator<CreateSeriesTransferRequest>
{
    public CreateSeriesTransferRequestValidator()
    {
        RuleFor(x => x.AbsSeriesId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.AbsLibraryId).NotEmpty().MaximumLength(256);

        RuleFor(x => x.OverrideMinAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMinAge.HasValue);

        RuleFor(x => x.OverrideMaxAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMaxAge.HasValue);
    }
}

/// <summary>
/// Phase 2: Validates batch transfer requests.
/// ISP: Separate validator for the batch-specific concerns (array bounds).
/// </summary>
public class BatchTransferRequestValidator : AbstractValidator<BatchTransferRequest>
{
    public BatchTransferRequestValidator()
    {
        RuleFor(x => x.AbsLibraryItemIds)
            .NotEmpty().WithMessage("At least one item is required")
            .Must(ids => ids.Length <= 50).WithMessage("Maximum 50 items per batch");

        RuleForEach(x => x.AbsLibraryItemIds)
            .NotEmpty().WithMessage("Item ID cannot be empty");

        RuleFor(x => x.OverrideMinAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMinAge.HasValue);

        RuleFor(x => x.OverrideMaxAge)
            .InclusiveBetween(0, 18)
            .When(x => x.OverrideMaxAge.HasValue);

        RuleFor(x => x)
            .Must(x => !x.OverrideMinAge.HasValue || !x.OverrideMaxAge.HasValue ||
                       x.OverrideMinAge < x.OverrideMaxAge)
            .WithMessage("Min age must be less than max age");
    }
}

public class CreatePlaylistRequestValidator : AbstractValidator<CreatePlaylistRequest>
{
    public CreatePlaylistRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
        RuleFor(x => x.DefaultGrouping).IsInEnum();
    }
}

public class AddPlaylistItemsRequestValidator : AbstractValidator<AddPlaylistItemsRequest>
{
    public AddPlaylistItemsRequestValidator()
    {
        RuleFor(x => x.AbsLibraryItemIds)
            .NotEmpty().WithMessage("At least one item is required")
            .Must(ids => ids.Length <= 100).WithMessage("Maximum 100 items per request");
        RuleForEach(x => x.AbsLibraryItemIds).NotEmpty().MaximumLength(256);
    }
}

public class AddPlaylistSeriesRequestValidator : AbstractValidator<AddPlaylistSeriesRequest>
{
    public AddPlaylistSeriesRequestValidator()
    {
        RuleFor(x => x.AbsSeriesId).NotEmpty().MaximumLength(256);
    }
}

public class ReorderPlaylistRequestValidator : AbstractValidator<ReorderPlaylistRequest>
{
    public ReorderPlaylistRequestValidator()
    {
        RuleFor(x => x.OrderedItemIds).NotEmpty().WithMessage("Item order is required");
    }
}

/// <summary>
/// Phase 3: Validates settings update requests.
/// SRP: Only validates age range constraints, not business logic.
/// </summary>
public class UpdateSettingsRequestValidator : AbstractValidator<UpdateSettingsRequest>
{
    public UpdateSettingsRequestValidator()
    {
        RuleFor(x => x.DefaultMinAge)
            .InclusiveBetween(0, 18)
            .When(x => x.DefaultMinAge.HasValue);

        RuleFor(x => x.DefaultMaxAge)
            .InclusiveBetween(0, 18)
            .When(x => x.DefaultMaxAge.HasValue);

        RuleFor(x => x)
            .Must(x => !x.DefaultMinAge.HasValue || !x.DefaultMaxAge.HasValue ||
                       x.DefaultMinAge < x.DefaultMaxAge)
            .WithMessage("Min age must be less than max age");
    }
}
