using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0361 / UC13 — validates the body accepted by <c>PUT /api/profile/contact</c>
/// (<see cref="ProfileContactInput"/>). Rules:
/// <list type="bullet">
///   <item><c>DisplayName</c> is required (non-whitespace).</item>
///   <item><c>Email</c>, when present, must validate as an RFC e-mail.</item>
///   <item><c>Phone</c>, when present, must match the E.164 shape; the
///         deeper canonical normalisation runs in the service layer via
///         <c>PhoneE164.TryCreate</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Auto-registered into DI by
/// <c>AddValidatorsFromAssemblyContaining&lt;ApplicationAssemblyMarker&gt;</c>
/// (see <c>InfrastructureServiceCollectionExtensions</c>); call sites resolve
/// it via the injected <c>IValidator&lt;ProfileContactInput&gt;</c>.
/// </remarks>
public sealed class ProfileContactInputValidator : AbstractValidator<ProfileContactInput>
{
    /// <summary>
    /// E.164 shape — leading <c>+</c>, then 2-15 digits. Matches the canonical
    /// form documented on <c>PhoneE164.TryCreate</c>; common formatting
    /// characters are NOT accepted here because the validator runs after
    /// the boundary normalises whitespace at the JSON serialiser layer.
    /// </summary>
    private const string PhoneE164Pattern = @"^\+[1-9][0-9]{1,14}$";

    /// <summary>Wires the rules at construction time.</summary>
    public ProfileContactInputValidator()
    {
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Display name is required.");

        RuleFor(x => x.Email)
            .EmailAddress()
            .When(x => !string.IsNullOrWhiteSpace(x.Email))
            .WithErrorCode(ErrorCodes.ValidationFailed)
            .WithMessage("Email must be a valid RFC address.");

        RuleFor(x => x.Phone)
            .Matches(PhoneE164Pattern)
            .When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithErrorCode(ErrorCodes.InvalidPhone)
            .WithMessage("Phone must be in canonical E.164 form (e.g. +37369123456).");
    }
}
