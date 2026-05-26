using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0051 / TOR SEC 014 — FluentValidation rule-set for <see cref="LocalLoginInputDto"/>.
/// Lives in the Application layer alongside the other input validators; consumed by
/// the <c>LocalLoginService</c> and the <c>AuthController.TokenAsync</c> handler at
/// the password-grant entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why minimal.</b> Unlike <see cref="PasswordPolicyValidator"/> (which gates
/// password CHANGE), this validator gates LOGIN. A user with a legacy weak password
/// must still be able to sign in to rotate it; the composition policy is therefore
/// NOT enforced here. We only bound the length so the Argon2id verification path
/// cannot be flooded with pathological input lengths.
/// </para>
/// <para>
/// <b>Stable error code.</b> Every rule reports <see cref="ErrorCodes.LoginInvalid"/>
/// — the same code returned by the service for unknown login / wrong password / non-
/// Active state / missing role. The wire response therefore never reveals which
/// specific validation rule tripped; client UX shows a generic "invalid credentials"
/// message and the audit row records the precise outcome for ops forensics.
/// </para>
/// </remarks>
public sealed class LocalLoginInputValidator : AbstractValidator<LocalLoginInputDto>
{
    /// <summary>Minimum acceptable login-handle length, in characters.</summary>
    private const int LoginMinLength = 3;

    /// <summary>Maximum acceptable login-handle length, in characters.</summary>
    private const int LoginMaxLength = 64;

    /// <summary>Minimum acceptable password length, in characters.</summary>
    private const int PasswordMinLength = 8;

    /// <summary>
    /// Maximum acceptable password length, in characters. Higher than the policy
    /// ceiling of 128 (which lives on password CHANGE) so the login path accepts any
    /// historical credential within the Argon2id verification budget.
    /// </summary>
    private const int PasswordMaxLength = 256;

    /// <summary>
    /// Matches the canonical login-handle shape — letters, digits, dot, underscore,
    /// hyphen. Anchored with <c>^...$</c> so embedded whitespace or punctuation
    /// outside the allow-list trips the rule.
    /// </summary>
    private static readonly Regex LoginPattern =
        new("^[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Creates the validator with every rule registered.</summary>
    public LocalLoginInputValidator()
    {
        RuleFor(x => x.Login)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.LoginInvalid)
            .WithMessage("Login is required.")
            .DependentRules(() =>
            {
                RuleFor(x => x.Login)
                    .MinimumLength(LoginMinLength)
                    .WithErrorCode(ErrorCodes.LoginInvalid)
                    .WithMessage($"Login must be at least {LoginMinLength} characters.");

                RuleFor(x => x.Login)
                    .MaximumLength(LoginMaxLength)
                    .WithErrorCode(ErrorCodes.LoginInvalid)
                    .WithMessage($"Login must be at most {LoginMaxLength} characters.");

                RuleFor(x => x.Login)
                    .Must(value => value is not null && LoginPattern.IsMatch(value))
                    .WithErrorCode(ErrorCodes.LoginInvalid)
                    .WithMessage("Login may contain letters, digits, dot, underscore, or hyphen only.");
            });

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.LoginInvalid)
            .WithMessage("Password is required.")
            .DependentRules(() =>
            {
                RuleFor(x => x.Password)
                    .MinimumLength(PasswordMinLength)
                    .WithErrorCode(ErrorCodes.LoginInvalid)
                    .WithMessage($"Password must be at least {PasswordMinLength} characters.");

                RuleFor(x => x.Password)
                    .MaximumLength(PasswordMaxLength)
                    .WithErrorCode(ErrorCodes.LoginInvalid)
                    .WithMessage($"Password must be at most {PasswordMaxLength} characters.");
            });
    }
}
