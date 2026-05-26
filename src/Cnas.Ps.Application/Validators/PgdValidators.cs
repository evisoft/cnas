using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0117 / CF 14.11 — validator for <see cref="PgdDatasetPublishInputDto"/>. Enforces
/// the boundary contract documented on the DTO: stable dataset code (≤ 64), bounded
/// title (≤ 200) / description (≤ 1000), payload capped at 1 MiB chars, content type
/// non-empty (≤ 100).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why cap PayloadJson at 1 MiB.</b> The payload travels as a single in-memory
/// string through the publish call; a hostile or buggy admin posting a hundred-MB
/// blob would block the request thread and balloon memory. The cap matches the
/// boundary documented on the DTO and is sufficient for the open-data datasets in
/// scope (statistical aggregates).
/// </para>
/// </remarks>
public sealed class PgdDatasetPublishInputDtoValidator : AbstractValidator<PgdDatasetPublishInputDto>
{
    /// <summary>Maximum dataset-code length.</summary>
    public const int MaxDatasetCodeLength = 64;

    /// <summary>Maximum title length.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum description length.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>Maximum payload length (1 MiB).</summary>
    public const int MaxPayloadLength = 1_048_576;

    /// <summary>Maximum content-type length.</summary>
    public const int MaxContentTypeLength = 100;

    /// <summary>Wires the rule set.</summary>
    public PgdDatasetPublishInputDtoValidator()
    {
        RuleFor(x => x.DatasetCode)
            .NotEmpty().WithMessage("DatasetCode is required.")
            .MaximumLength(MaxDatasetCodeLength)
            .WithMessage($"DatasetCode must be ≤ {MaxDatasetCodeLength} characters.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(MaxTitleLength)
            .WithMessage($"Title must be ≤ {MaxTitleLength} characters.");

        RuleFor(x => x.Description)
            .NotNull().WithMessage("Description is required (may be empty).")
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Description must be ≤ {MaxDescriptionLength} characters.");

        RuleFor(x => x.PayloadJson)
            .NotEmpty().WithMessage("PayloadJson is required.")
            .Must(p => p is not null && p.Length <= MaxPayloadLength)
            .WithMessage($"PayloadJson must be ≤ {MaxPayloadLength} characters.");

        RuleFor(x => x.ContentType)
            .NotEmpty().WithMessage("ContentType is required.")
            .MaximumLength(MaxContentTypeLength)
            .WithMessage($"ContentType must be ≤ {MaxContentTypeLength} characters.");
    }
}
