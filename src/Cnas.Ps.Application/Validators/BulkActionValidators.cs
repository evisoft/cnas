using System.Text.RegularExpressions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — FluentValidation rules for
/// <see cref="BulkSelectionCreateDto"/>. Enforces the registry allow-list, the
/// filter-payload size cap, and the per-list explicit-id cap. Note that the
/// Sqid-decoding pass happens at the controller boundary (controllers can return
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.InvalidId"/> with a structured
/// ProblemDetails); this validator runs on the already-decoded strings and only checks
/// shape.
/// </summary>
public sealed class BulkSelectionCreateDtoValidator : AbstractValidator<BulkSelectionCreateDto>
{
    /// <summary>
    /// Hard cap on the byte length of <see cref="BulkSelectionCreateDto.FilterJson"/>.
    /// Mirrors the service-layer cap (default 8192) so the validator surfaces a tight
    /// error before the service layer is reached.
    /// </summary>
    internal const int FilterJsonByteCap = 8192;

    /// <summary>
    /// Hard cap on the size of <see cref="BulkSelectionCreateDto.ExplicitIncludeIds"/> /
    /// <see cref="BulkSelectionCreateDto.ExplicitExcludeIds"/>. Mirrors
    /// <c>BulkSelectionOptions.MaxExplicitIdsPerList</c> (default 5 000).
    /// </summary>
    internal const int ExplicitIdsCap = 5_000;

    /// <summary>
    /// Pattern matched by every Sqid the controller has not yet decoded. Sqids are
    /// alphanumeric with no separators; the upper bound (64 chars) defends against
    /// runaway payloads. The validator does not attempt to decode here — the
    /// controller does, and rejects malformed values with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.InvalidId"/>.
    /// </summary>
    internal const string SqidPattern = "^[A-Za-z0-9]{1,64}$";

    /// <summary>Creates the validator.</summary>
    public BulkSelectionCreateDtoValidator()
    {
        RuleFor(x => x.Registry)
            .NotEmpty().WithMessage("Registry is required.")
            .Must(BulkRegistries.IsKnown)
            .WithMessage($"Registry must be one of: {string.Join(", ", BulkRegistries.All)}.");

        RuleFor(x => x.FilterJson)
            .NotEmpty().WithMessage("FilterJson is required.")
            .Must(json => json is null || System.Text.Encoding.UTF8.GetByteCount(json) <= FilterJsonByteCap)
            .WithMessage($"FilterJson exceeds the {FilterJsonByteCap}-byte cap.");

        RuleFor(x => x.ExplicitIncludeIds)
            .Must(list => list is null || list.Count <= ExplicitIdsCap)
            .WithMessage($"ExplicitIncludeIds exceeds the {ExplicitIdsCap}-entry cap.")
            .ForEach(id => id.Matches(SqidPattern)
                .WithMessage("ExplicitIncludeIds must contain only Sqid strings (alphanumeric, ≤64 chars)."));

        RuleFor(x => x.ExplicitExcludeIds)
            .Must(list => list is null || list.Count <= ExplicitIdsCap)
            .WithMessage($"ExplicitExcludeIds exceeds the {ExplicitIdsCap}-entry cap.")
            .ForEach(id => id.Matches(SqidPattern)
                .WithMessage("ExplicitExcludeIds must contain only Sqid strings (alphanumeric, ≤64 chars)."));
    }
}

/// <summary>
/// R0166 — FluentValidation rules for <see cref="BulkOperationRunCreateDto"/>.
/// Enforces the operation-code shape, the idempotency-key format, and the BulkSelectionId
/// presence; semantic checks (operation exists in the registry, selection valid for
/// caller, etc.) happen inside the runner.
/// </summary>
public sealed class BulkOperationRunCreateDtoValidator : AbstractValidator<BulkOperationRunCreateDto>
{
    /// <summary>Stable shape of an operation code (PascalCase first segment, dots allowed). Anchored.</summary>
    internal const string OperationCodePattern = "^[A-Z][A-Za-z0-9.]+$";

    /// <summary>Stable shape of an idempotency key — ASCII letters/digits/dash/underscore, 1-128 chars.</summary>
    internal const string IdempotencyKeyPattern = "^[A-Za-z0-9_-]{1,128}$";

    /// <summary>Hard upper bound on the size of <c>ParametersJson</c>. Mirrors the filter cap.</summary>
    internal const int ParametersJsonByteCap = 8192;

    /// <summary>Creates the validator.</summary>
    public BulkOperationRunCreateDtoValidator()
    {
        RuleFor(x => x.BulkSelectionId)
            .NotEmpty().WithMessage("BulkSelectionId is required.")
            .Matches(BulkSelectionCreateDtoValidator.SqidPattern)
            .WithMessage("BulkSelectionId must be a Sqid string.");

        RuleFor(x => x.OperationCode)
            .NotEmpty().WithMessage("OperationCode is required.")
            .Matches(OperationCodePattern)
            .WithMessage("OperationCode must match ^[A-Z][A-Za-z0-9.]+$.");

        RuleFor(x => x.ParametersJson)
            .Must(p => p is null || System.Text.Encoding.UTF8.GetByteCount(p) <= ParametersJsonByteCap)
            .WithMessage($"ParametersJson exceeds the {ParametersJsonByteCap}-byte cap.");

        RuleFor(x => x.IdempotencyKey)
            .Matches(IdempotencyKeyPattern)
            .When(x => !string.IsNullOrEmpty(x.IdempotencyKey))
            .WithMessage("IdempotencyKey must be ≤128 chars of ASCII letters/digits/dash/underscore.");
    }
}

/// <summary>
/// R0166 — shared compiled regex helpers used by the bulk-action validators and the
/// runner's defensive re-validation pass. Public because the Infrastructure-layer
/// registry consumes the operation-code regex when validating registered operations
/// at host startup.
/// </summary>
public static class BulkActionPatterns
{
    /// <summary>Compiled operation-code regex with a 50 ms backtracking budget.</summary>
    public static readonly Regex OperationCode = new(
        BulkOperationRunCreateDtoValidator.OperationCodePattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Compiled idempotency-key regex with a 50 ms backtracking budget.</summary>
    public static readonly Regex IdempotencyKey = new(
        BulkOperationRunCreateDtoValidator.IdempotencyKeyPattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));
}
