using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0302 / TOR §2.1 — record carrying the validated arguments of an
/// <c>IContributorSourceHistoryService.RecordChangeAsync</c> call. Lifted into a
/// DTO so FluentValidation can apply uniform rules at the service boundary.
/// </summary>
/// <param name="ContributorId">Internal contributor primary key (must be &gt; 0).</param>
/// <param name="OldSourceSystem">Prior source value; nullable, ≤ 64 chars.</param>
/// <param name="NewSourceSystem">New source value; required, 1..64 chars.</param>
/// <param name="Reason">Optional operator-supplied justification, ≤ 500 chars.</param>
public sealed record ContributorSourceChangeArgs(
    long ContributorId,
    string? OldSourceSystem,
    string NewSourceSystem,
    string? Reason);

/// <summary>
/// R0302 — FluentValidation rules for <see cref="ContributorSourceChangeArgs"/>.
/// </summary>
public sealed class ContributorSourceChangeArgsValidator : AbstractValidator<ContributorSourceChangeArgs>
{
    /// <summary>Maximum length of a SourceSystem string (chars).</summary>
    public const int SourceSystemMaxLength = 64;

    /// <summary>Maximum length of a Reason string (chars).</summary>
    public const int ReasonMaxLength = 500;

    /// <summary>Wires the rules at construction time.</summary>
    public ContributorSourceChangeArgsValidator()
    {
        RuleFor(x => x.ContributorId)
            .GreaterThan(0L)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("ContributorId must be positive.");

        RuleFor(x => x.NewSourceSystem)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("NewSourceSystem is required.")
            .MaximumLength(SourceSystemMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"NewSourceSystem must be ≤ {SourceSystemMaxLength} characters.");

        RuleFor(x => x.OldSourceSystem)
            .MaximumLength(SourceSystemMaxLength)
            .When(x => x.OldSourceSystem is not null)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"OldSourceSystem must be ≤ {SourceSystemMaxLength} characters when supplied.");

        RuleFor(x => x.Reason)
            .MaximumLength(ReasonMaxLength)
            .When(x => x.Reason is not null)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Reason must be ≤ {ReasonMaxLength} characters when supplied.");
    }
}

/// <summary>
/// R0302 — query parameters for <c>GET /api/contributors/{sqid}/source-history</c>.
/// </summary>
/// <param name="Skip">0-based offset (≥ 0).</param>
/// <param name="Take">Page size, 1..200.</param>
public sealed record ContributorSourceHistoryQueryDto(int Skip, int Take);

/// <summary>
/// R0302 — FluentValidation rules for <see cref="ContributorSourceHistoryQueryDto"/>.
/// </summary>
public sealed class ContributorSourceHistoryQueryDtoValidator : AbstractValidator<ContributorSourceHistoryQueryDto>
{
    /// <summary>Maximum page size accepted at the boundary.</summary>
    public const int MaxTake = 200;

    /// <summary>Wires the rules at construction time.</summary>
    public ContributorSourceHistoryQueryDtoValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Skip must be >= 0.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Take must be in [1, {MaxTake}].");
    }
}
