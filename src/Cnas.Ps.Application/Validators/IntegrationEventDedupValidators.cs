using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0103 / TOR CF 14.02 — input arguments accepted by
/// <c>IIntegrationEventDeduper.TryClaimAsync</c>, lifted into a record so
/// FluentValidation can apply uniform rules. Per CLAUDE.md §2.5 (Input
/// Validation) the deduper validates at its own boundary because it sits at
/// the trust edge between transport and application.
/// </summary>
/// <param name="MessageId">
/// CloudEvents v1.0 <c>id</c> attribute. 1..128 characters, non-whitespace.
/// </param>
/// <param name="Source">
/// CloudEvents v1.0 <c>source</c> attribute. 1..256 characters, non-whitespace.
/// </param>
/// <param name="Type">
/// CloudEvents v1.0 <c>type</c> attribute. 1..256 characters, non-whitespace.
/// </param>
public sealed record IntegrationEventDedupClaimArgs(
    string MessageId,
    string Source,
    string Type);

/// <summary>
/// R0103 / TOR CF 14.02 — FluentValidation rules for the
/// <see cref="IntegrationEventDedupClaimArgs"/> tuple. Reports
/// <see cref="ErrorCodes.ValidationFailed"/> on every failed rule so the
/// caller can collapse validation errors uniformly.
/// </summary>
public sealed class IntegrationEventDedupClaimArgsValidator : AbstractValidator<IntegrationEventDedupClaimArgs>
{
    /// <summary>Maximum supported MessageId length (chars).</summary>
    public const int MessageIdMaxLength = 128;

    /// <summary>Maximum supported Source length (chars).</summary>
    public const int SourceMaxLength = 256;

    /// <summary>Maximum supported Type length (chars).</summary>
    public const int TypeMaxLength = 256;

    /// <summary>Wires the rules at construction time.</summary>
    public IntegrationEventDedupClaimArgsValidator()
    {
        RuleFor(x => x.MessageId)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("MessageId is required.")
            .MaximumLength(MessageIdMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"MessageId must be at most {MessageIdMaxLength} characters.");

        RuleFor(x => x.Source)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Source is required.")
            .MaximumLength(SourceMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Source must be at most {SourceMaxLength} characters.");

        RuleFor(x => x.Type)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Type is required.")
            .MaximumLength(TypeMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"Type must be at most {TypeMaxLength} characters.");
    }
}

/// <summary>
/// R0103 / TOR CF 14.02 — argument record consumed by the
/// <c>IIntegrationEventDeduper.MarkFailedAsync</c> validator.
/// </summary>
/// <param name="MessageId">MessageId whose row to flip to Failed. Required, ≤128.</param>
/// <param name="FailureReason">
/// Sanitised single-line description. Required, ≤1000 chars. The writer
/// truncates / sanitises before persistence; the validator only enforces the
/// upper bound and non-emptiness.
/// </param>
public sealed record IntegrationEventDedupMarkFailedArgs(
    string MessageId,
    string FailureReason);

/// <summary>
/// R0103 / TOR CF 14.02 — FluentValidation rules for
/// <see cref="IntegrationEventDedupMarkFailedArgs"/>.
/// </summary>
public sealed class IntegrationEventDedupMarkFailedArgsValidator : AbstractValidator<IntegrationEventDedupMarkFailedArgs>
{
    /// <summary>Maximum supported FailureReason length (chars).</summary>
    public const int FailureReasonMaxLength = 1000;

    /// <summary>Wires the rules at construction time.</summary>
    public IntegrationEventDedupMarkFailedArgsValidator()
    {
        RuleFor(x => x.MessageId)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("MessageId is required.")
            .MaximumLength(IntegrationEventDedupClaimArgsValidator.MessageIdMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"MessageId must be at most {IntegrationEventDedupClaimArgsValidator.MessageIdMaxLength} characters.");

        RuleFor(x => x.FailureReason)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("FailureReason is required.")
            .MaximumLength(FailureReasonMaxLength)
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage($"FailureReason must be at most {FailureReasonMaxLength} characters.");
    }
}
