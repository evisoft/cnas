using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// Validates a <see cref="PasswordInput"/> against the local-login password policy
/// per CLAUDE.md §5.3, TOR SEC 014, and R0052: minimum 8 / maximum 128 characters,
/// at least one lowercase letter, one uppercase letter, one digit, and one symbol.
/// </summary>
/// <remarks>
/// <para>
/// Used by the local <c>Utilizator autorizat</c> credential surface only — citizens
/// authenticate via MPass SAML and never touch this validator. Hooked into the
/// pipeline through the standard FluentValidation DI registration in
/// <see cref="ApplicationServiceCollectionExtensions"/>.
/// </para>
/// <para>
/// Every rule reports <see cref="ErrorCodes.PasswordPolicyViolation"/> as its stable
/// machine-readable code so callers can branch on a single identifier; the per-rule
/// human-readable <c>WithMessage</c> text drives the UI prompt for the specific
/// failure. Hashing happens downstream via <c>IPasswordHasher</c> — never inside
/// this validator (separation of concerns: validation = "is this acceptable",
/// hashing = "produce the storable form").
/// </para>
/// <para>
/// The maximum-length cap (128) is a defense against pathological inputs that
/// would force the Argon2id memory budget into denial-of-service territory; users
/// in practice never type strings of this size, but the limit is explicit so the
/// behaviour is predictable.
/// </para>
/// </remarks>
public sealed class PasswordPolicyValidator : AbstractValidator<PasswordInput>
{
    /// <summary>Minimum acceptable plaintext length, in characters.</summary>
    private const int MinLength = 8;

    /// <summary>Maximum acceptable plaintext length, in characters.</summary>
    private const int MaxLength = 128;

    /// <summary>Matches when the input contains at least one ASCII lowercase letter.</summary>
    private static readonly Regex LowercasePattern =
        new("[a-z]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Matches when the input contains at least one ASCII uppercase letter.</summary>
    private static readonly Regex UppercasePattern =
        new("[A-Z]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Matches when the input contains at least one ASCII digit.</summary>
    private static readonly Regex DigitPattern =
        new("[0-9]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Matches when the input contains at least one symbol — defined as any character
    /// that is NOT in the alphanumeric set <c>[A-Za-z0-9]</c>. This permissive definition
    /// accepts every printable punctuation mark, Unicode symbol, or whitespace as a
    /// symbol; we deliberately do not pin a specific symbol allow-list because doing so
    /// reduces the keyspace.
    /// </summary>
    private static readonly Regex SymbolPattern =
        new("[^A-Za-z0-9]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Creates the validator with all per-rule predicates registered.</summary>
    public PasswordPolicyValidator()
    {
        RuleFor(x => x.Plaintext)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
            .WithMessage("Password is required.")
            // Subsequent rules cascade only when the value is present — they assume a
            // non-null, non-empty string and would otherwise produce duplicate noise.
            .DependentRules(() =>
            {
                RuleFor(x => x.Plaintext)
                    .MinimumLength(MinLength)
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage($"Password must be at least {MinLength} characters.");

                RuleFor(x => x.Plaintext)
                    .MaximumLength(MaxLength)
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage($"Password must be at most {MaxLength} characters.");

                RuleFor(x => x.Plaintext)
                    .Must(value => value is not null && LowercasePattern.IsMatch(value))
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage("Password must contain at least one lowercase letter.");

                RuleFor(x => x.Plaintext)
                    .Must(value => value is not null && UppercasePattern.IsMatch(value))
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage("Password must contain at least one uppercase letter.");

                RuleFor(x => x.Plaintext)
                    .Must(value => value is not null && DigitPattern.IsMatch(value))
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage("Password must contain at least one digit.");

                RuleFor(x => x.Plaintext)
                    .Must(value => value is not null && SymbolPattern.IsMatch(value))
                    .WithErrorCode(ErrorCodes.PasswordPolicyViolation)
                    .WithMessage("Password must contain at least one symbol.");
            });
    }
}
