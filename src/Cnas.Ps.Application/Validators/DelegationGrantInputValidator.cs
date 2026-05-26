using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — validates the <see cref="DelegationGrantInputDto"/>
/// before the lifecycle service persists a grant. Encodes the operational window cap
/// (≤ 90 days) and the scope length contract; deeper identity checks (delegatee
/// exists, ≠ grantor) live in the service because they require a DB lookup.
/// </summary>
public sealed class DelegationGrantInputValidator : AbstractValidator<DelegationGrantInputDto>
{
    /// <summary>Operational cap on a single delegation window — see R0057 / SEC 026.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    /// <summary>Constructs the validator with every field rule wired in.</summary>
    public DelegationGrantInputValidator()
    {
        // DelegateeSqid: required + non-empty. The service decodes and rejects unknown
        // ids with NotFound; the validator only guards against trivially-blank input.
        RuleFor(x => x.DelegateeSqid)
            .NotEmpty().WithMessage("DelegateeSqid is required.");

        // Scope: required, capped at 128 chars (mirrors the EF column).
        RuleFor(x => x.Scope)
            .NotEmpty().WithMessage("Scope is required.")
            .MaximumLength(128).WithMessage("Scope must be 128 characters or fewer.");

        // Window: forward-only AND ≤ 90 days. We check both bounds in a single rule so
        // the failure message can name the offending bound; FluentValidation surfaces
        // the first failing rule per property and we want the user to see the precise
        // reason.
        RuleFor(x => x)
            .Must(HaveForwardOnlyWindow)
            .WithMessage("ValidToUtc must be greater than ValidFromUtc.")
            .Must(HaveWindowWithinCap)
            .WithMessage($"Delegation window must be {MaxWindow.TotalDays:F0} days or fewer.");
    }

    /// <summary>
    /// Returns <see langword="true"/> when <c>ValidToUtc</c> is strictly after
    /// <c>ValidFromUtc</c>. Equal bounds are rejected — a zero-length window has no
    /// business meaning.
    /// </summary>
    /// <param name="dto">Input being validated.</param>
    /// <returns><see langword="true"/> when the window has positive length.</returns>
    private static bool HaveForwardOnlyWindow(DelegationGrantInputDto dto) =>
        dto.ValidToUtc > dto.ValidFromUtc;

    /// <summary>
    /// Returns <see langword="true"/> when the window fits inside the
    /// <see cref="MaxWindow"/> operational cap. Inverted bounds (caught by
    /// <see cref="HaveForwardOnlyWindow"/>) trivially pass this check; the prior rule
    /// surfaces the more specific message.
    /// </summary>
    /// <param name="dto">Input being validated.</param>
    /// <returns><see langword="true"/> when the window is within the cap.</returns>
    private static bool HaveWindowWithinCap(DelegationGrantInputDto dto) =>
        dto.ValidToUtc - dto.ValidFromUtc <= MaxWindow;
}

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — validates the
/// <see cref="DelegationGrantRevokeInputDto"/> body before the lifecycle service
/// records a revocation. Reason 3..500 chars mirrors the cap enforced by the EF
/// <c>DelegationGrantConfiguration</c>.
/// </summary>
public sealed class DelegationGrantRevokeInputValidator : AbstractValidator<DelegationGrantRevokeInputDto>
{
    /// <summary>Constructs the validator with the reason-length rule wired in.</summary>
    public DelegationGrantRevokeInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}
