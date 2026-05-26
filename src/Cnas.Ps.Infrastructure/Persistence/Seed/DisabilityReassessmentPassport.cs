using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-C — Reevaluare a gradului de dizabilitate
/// (Disability reassessment) seed row. Eligibility requires that the claimant
/// already has a disability record on file, is still insured, and that the
/// reassessment is due (more than one year has elapsed since the last review).
/// The payload is a decision-only outcome with no monetary payout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine limitation:</b> the current <c>JsonRulesDecisionEngine</c> supports
/// <c>date-within-days</c> as "subject is within N days of reference" but lacks an
/// inverse "subject is older than N days" operator. The natural rule for
/// reassessment-due — "<c>lastReassessmentDateUtc</c> is more than 365 days before
/// <c>claimDateUtc</c>" — therefore cannot be expressed directly with the existing
/// six rule kinds. As a workaround, this passport requires the caller to pre-compute
/// a boolean fact <c>reassessmentDue</c> (true when the threshold has elapsed) and
/// the rule checks it via <c>fact-equals true</c>. See <c>TODO.md §17</c> for the
/// follow-up to add a <c>date-older-than-days</c> rule kind.
/// </para>
/// <para>
/// TOR §3.3-C. The fixed-amount payload of 0 MDL reflects the administrative-only
/// nature of the reassessment workflow; pension recalculation, if any, is emitted
/// by a downstream service after the commission delivers the new degree.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityReassessmentPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityReassessmentPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-C-DISABILITY-REASSESSMENT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Reevaluare a gradului de dizabilitate",
      "type": "object",
      "required": ["existingDisabilityRecord", "reassessmentDue", "isInsured"],
      "properties": {
        "existingDisabilityRecord": { "type": "boolean" },
        "reassessmentDue":          { "type": "boolean" },
        "isInsured":                { "type": "boolean" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the claimant has an existing disability record, that the reassessment
    /// window has elapsed, and that the claimant is still insured; the payload
    /// is a fixed 0 MDL marker (decision only — no payout).
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_REASSESSMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "existingDisabilityRecord", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NO_RECORD" },
        { "rule": "fact-equals", "fact": "reassessmentDue", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NOT_DUE" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_REASSESSMENT_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "DISABILITY_REASSESSMENT_ELIGIBLE"
    }
    """;

    /// <summary>
    /// Builds a fully-populated <see cref="ServicePassport"/> seed row stamped with
    /// the supplied clock's <c>UtcNow</c>.
    /// </summary>
    /// <param name="clock">Clock abstraction (UTC) used to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>A new <see cref="ServicePassport"/> ready to be inserted into the DB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    /// <example>
    /// <code>
    /// var passport = DisabilityReassessmentPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Reevaluare a gradului de dizabilitate",
            NameEn = "Disability reassessment",
            NameRu = "Переосвидетельствование инвалидности",
            DescriptionRo =
                "Reevaluare periodică a gradului de dizabilitate, declanșată după expirarea " +
                "termenului fixat de comisia medicală, conform Legii 60/2012.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-REASSESSMENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
