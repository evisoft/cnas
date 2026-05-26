using System;
using System.Text.Json;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0141 / TOR CF 15.03 — validates <see cref="BusinessRuleInputDto"/> before the
/// editor service persists it. Beyond the per-field rules (name length, optional
/// id sqid shape, applicant-type enum membership, notes length) the validator
/// also asserts that the supplied <c>ConditionJson</c> is well-formed JSON. The
/// deeper engine-level parse (rule kind recognition, fact-name references) is
/// the service layer's responsibility — this validator only guarantees the
/// payload reaches the editor service in a structurally-sane shape so the
/// service can re-use the JSON-rules engine parser without first defending
/// against junk input.
/// </summary>
public sealed class BusinessRuleInputValidator : AbstractValidator<BusinessRuleInputDto>
{
    /// <summary>Constructs the validator with every field rule wired in.</summary>
    public BusinessRuleInputValidator()
    {
        // Name: 3..256, non-empty. Mirrors the AuditCategory / SupportTicketCategory
        // display-name bounds so the admin UI can re-use the same form-component
        // limits across the registries.
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MinimumLength(3).WithMessage("Name must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Name must be 256 characters or fewer.");

        // ApplicantType: stable enum membership. FluentValidation's
        // IsInEnum() correctly rejects out-of-range integer casts.
        RuleFor(x => x.ApplicantType)
            .IsInEnum().WithMessage("ApplicantType must be Natural, Legal, or Both.");

        // DecisionOutcome: stable enum membership.
        RuleFor(x => x.DecisionOutcome)
            .IsInEnum().WithMessage("DecisionOutcome must be Granted, Rejected, or RequiresReview.");

        // ConditionJson: required + well-formed JSON. We do NOT require a
        // specific shape here — the editor service consults the JSON-rules
        // engine parser for the deeper structural check (rule-kind recognition,
        // operand types). This rule is the cheap fail-fast guard.
        RuleFor(x => x.ConditionJson)
            .NotEmpty().WithMessage("ConditionJson is required.")
            .MaximumLength(8000).WithMessage("ConditionJson must be 8000 characters or fewer.")
            .Must(BeWellFormedJson)
            .WithMessage("ConditionJson must be well-formed JSON.");

        // Notes: optional, max 2000 chars.
        RuleFor(x => x.Notes)
            .MaximumLength(2000).When(x => x.Notes is not null)
            .WithMessage("Notes must be 2000 characters or fewer.");

        // Id: when supplied (update branch), the editor service decides whether
        // the value resolves to an existing rule. We only enforce a minimum
        // length so a "  " never reaches the service. Null is acceptable
        // (create branch).
        RuleFor(x => x.Id)
            .MinimumLength(1).When(x => x.Id is not null)
            .WithMessage("Id must be non-empty when supplied.");
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="json"/> parses as a
    /// well-formed JSON document. Returns <see langword="false"/> on null,
    /// whitespace, or any <see cref="JsonException"/>.
    /// </summary>
    /// <param name="json">Candidate JSON text.</param>
    /// <returns><see langword="true"/> when parseable.</returns>
    private static bool BeWellFormedJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
