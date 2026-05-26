using System.Text.Json;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2163 / INT 004 — validates <see cref="NewServiceProvisionInputDto"/> at the boundary
/// of the schema-driven new-service provisioning surface
/// (<c>POST /api/admin/service-catalog/provision</c>).
/// </summary>
/// <remarks>
/// <para>
/// The validator enforces TOR §15.4 INT 004's "new web-services without code changes"
/// contract: a stable upper-case code (3-32 chars), a Romanian display name (3-256
/// chars), a workflow code reference (non-empty), an SLA window (1-365 days), a JSON-
/// schema describing form fields (at least one property declared under
/// <c>properties</c>) and a non-null decision rules JSON. The cross-reference checks
/// (workflow code must exist, classifier schemes must be known) live in the service
/// layer because they need DB access — validators stay shape-only.
/// </para>
/// </remarks>
public sealed class NewServiceProvisionInputValidator : AbstractValidator<NewServiceProvisionInputDto>
{
    /// <summary>Builds the validator with the shape rules required by INT 004.</summary>
    public NewServiceProvisionInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .Length(3, 32)
            .Matches("^[A-Z0-9_\\-]+$")
            .WithMessage("Code must be uppercase letters, digits, dashes or underscores (3-32 chars).");

        RuleFor(x => x.NameRo)
            .NotEmpty()
            .Length(3, 256);

        RuleFor(x => x.NameEn)
            .MaximumLength(256);

        RuleFor(x => x.NameRu)
            .MaximumLength(256);

        RuleFor(x => x.DescriptionRo)
            .NotEmpty();

        RuleFor(x => x.WorkflowCode)
            .NotEmpty()
            .MaximumLength(64);

        RuleFor(x => x.MaxProcessingDays)
            .InclusiveBetween(1, 365);

        RuleFor(x => x.FormSchemaJson)
            .NotEmpty()
            .Must(HaveAtLeastOneFormField)
            .WithMessage("FormSchemaJson must declare at least one form field under 'properties'.");

        RuleFor(x => x.DecisionRulesJson)
            .NotNull()
            .WithMessage("DecisionRulesJson must not be null; supply '{}' for an empty rule-set.");
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="formSchemaJson"/> is a parseable
    /// JSON document whose root object declares at least one named entry under a
    /// <c>properties</c> object. JSON parse errors return <see langword="false"/> so the
    /// caller can correct the shape; payloads without <c>properties</c> are also
    /// rejected because the INT 004 schema-driven path needs at least one field to render
    /// a form against.
    /// </summary>
    /// <param name="formSchemaJson">Raw JSON payload supplied by the caller.</param>
    /// <returns><see langword="true"/> if the schema declares ≥1 form field; otherwise <see langword="false"/>.</returns>
    private static bool HaveAtLeastOneFormField(string formSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(formSchemaJson))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(formSchemaJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!root.TryGetProperty("properties", out var properties))
            {
                return false;
            }
            if (properties.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            return properties.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
