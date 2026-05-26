using Cnas.Ps.Contracts;
using Cnas.Ps.Core.ValueObjects;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// Validates <see cref="SubmitApplicationInput"/> at the API boundary per CLAUDE.md §2.5.
/// Errors carry field-level details for the caller to surface in the UI.
/// </summary>
public sealed class SubmitApplicationInputValidator : AbstractValidator<SubmitApplicationInput>
{
    /// <summary>Creates the validator with all rules in place.</summary>
    public SubmitApplicationInputValidator()
    {
        RuleFor(x => x.ServicePassportId)
            .NotEmpty()
            .Length(4, 64)
            .WithMessage("Service passport id is required.");

        RuleFor(x => x.FormPayloadJson)
            .NotEmpty()
            .Must(BeJsonObject)
            .WithMessage("Form payload must be a JSON object.");

        RuleFor(x => x.AttachmentDocumentIds)
            .NotNull()
            .ForEach(child => child.NotEmpty().Length(4, 64));

        // Optional delegation field — only validated when supplied (operator-on-behalf-of
        // flow per UC06 CF 06.02). Skipping the rule for null/empty preserves backward
        // compatibility with callers that submit on their own behalf.
        RuleFor(x => x.OnBehalfOfPrincipalIdnp)
            .Must(BeValidIdnp!)
            .When(x => !string.IsNullOrWhiteSpace(x.OnBehalfOfPrincipalIdnp))
            .WithMessage("OnBehalfOfPrincipalIdnp must be a valid IDNP (13 digits + mod-10 checksum).");
    }

    private static bool BeJsonObject(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        var trimmed = payload.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{';
    }

    /// <summary>True when <paramref name="candidate"/> is a structurally valid Moldovan IDNP.</summary>
    private static bool BeValidIdnp(string? candidate) => Idnp.TryCreate(candidate).IsSuccess;
}
