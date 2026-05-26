using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2263 / SEC 016 — validator for <see cref="UserAccountStateBulkInputDto"/> shared
/// by <c>POST /api/users/bulk-suspend</c> and <c>POST /api/users/bulk-unlock</c>.
/// Enforces:
/// <list type="bullet">
///   <item>UserSqids: 1..200 entries.</item>
///   <item>UserSqids: each entry alphanumeric (Sqid shape).</item>
///   <item>Reason: 3..500 chars.</item>
/// </list>
/// </summary>
public sealed class UserAccountStateBulkInputDtoValidator : AbstractValidator<UserAccountStateBulkInputDto>
{
    /// <summary>Minimum entries in the input list. Empty calls are rejected at the boundary.</summary>
    public const int MinUserSqids = 1;

    /// <summary>
    /// Maximum entries per call. Caps the blast radius of a single bulk action —
    /// operators can chain multiple calls if they need to flip more users, but
    /// each call writes one audit row per row so the cap also protects the audit
    /// table from runaway growth.
    /// </summary>
    public const int MaxUserSqids = 200;

    /// <summary>Minimum reason length — blocks trivial "x" / empty justifications.</summary>
    public const int MinReasonLength = 3;

    /// <summary>Maximum reason length — mirrors the audit payload's reason field cap.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Stable shape of a Sqid string — alphanumeric, ≤64 chars.</summary>
    internal const string SqidPattern = "^[A-Za-z0-9]{1,64}$";

    /// <summary>Wires the rule set.</summary>
    public UserAccountStateBulkInputDtoValidator()
    {
        RuleFor(x => x.UserSqids)
            .NotNull().WithMessage("UserSqids is required.")
            .Must(list => list is not null && list.Count >= MinUserSqids)
            .WithMessage($"UserSqids must contain at least {MinUserSqids} entry.")
            .Must(list => list is not null && list.Count <= MaxUserSqids)
            .WithMessage($"UserSqids exceeds the {MaxUserSqids}-entry cap.")
            .ForEach(sqid => sqid.Matches(SqidPattern)
                .WithMessage("Each UserSqid must be a Sqid (alphanumeric, ≤64 chars)."));

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(MinReasonLength)
            .MaximumLength(MaxReasonLength)
            .WithMessage($"Reason must be {MinReasonLength}..{MaxReasonLength} chars.");
    }
}
