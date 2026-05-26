using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0362 — validator for <see cref="ProfileUpdateRequestSubmitDto"/>. Enforces three
/// rules at submit time:
/// <list type="bullet">
///   <item><c>TargetContributorSqid</c> non-empty (decoding happens in the service).</item>
///   <item><c>Type</c> parses to one of the <see cref="ProfileUpdateRequestType"/> values.</item>
///   <item><c>RequestedChangesJson</c> is syntactically valid JSON. Structural shape
///     validation (matching the input-DTO shape for <c>Type</c>) is deferred to the
///     approval-time apply step so that submissions are never rejected on a
///     shape-mismatch the approver could fix.</item>
/// </list>
/// </summary>
public sealed class ProfileUpdateRequestSubmitDtoValidator : AbstractValidator<ProfileUpdateRequestSubmitDto>
{
    /// <summary>Allowed Type strings (case-insensitive). Mirror of <see cref="ProfileUpdateRequestType"/>.</summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ProfileUpdateRequestType.Address),
        nameof(ProfileUpdateRequestType.Contact),
        nameof(ProfileUpdateRequestType.CivilStatus),
        nameof(ProfileUpdateRequestType.Activity),
        nameof(ProfileUpdateRequestType.SocialInsuranceContract),
    };

    /// <summary>Constructs the validator.</summary>
    public ProfileUpdateRequestSubmitDtoValidator()
    {
        RuleFor(x => x.TargetContributorSqid)
            .NotEmpty()
            .WithMessage("TargetContributorSqid is required.");

        RuleFor(x => x.Type)
            .NotEmpty()
            .Must(v => v is not null && AllowedTypes.Contains(v))
            .WithMessage($"Type must be one of: {string.Join(", ", AllowedTypes)}.");

        RuleFor(x => x.RequestedChangesJson)
            .NotEmpty()
            .Must(IsValidJson)
            .WithMessage("RequestedChangesJson must be syntactically valid JSON.");

        RuleFor(x => x.Note)
            .MaximumLength(2000);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="json"/> can be parsed by
    /// <see cref="JsonDocument"/> as a non-empty document. Empty / null input fails the
    /// preceding <c>NotEmpty</c> rule so it is treated as invalid here too.
    /// </summary>
    /// <param name="json">Candidate JSON string.</param>
    private static bool IsValidJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
