using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-B — Pensie de dizabilitate cauzată de accident de
/// muncă sau boală profesională (Work-accident disability pension) seed row.
/// Eligibility requires that the claimant is insured, that the accident has been
/// verified by the medical commission, and that the disability degree is one of
/// the recognized values. The benefit is a fixed amount keyed by disability degree.
/// </summary>
/// <remarks>
/// TOR §3.3-B. The tier table (3 000 / 2 200 / 1 500 MDL per degree) is a
/// reasonable Moldovan default ordered by severity and can be tuned via passport
/// upsert when indexation rules change.
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityWorkAccidentPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityWorkAccidentPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-B-DISABILITY-WORK-ACCIDENT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de dizabilitate (accident de muncă)",
      "type": "object",
      "required": ["isInsured", "accidentVerifiedByCommission", "disabilityDegree"],
      "properties": {
        "isInsured":                     { "type": "boolean" },
        "accidentVerifiedByCommission":  { "type": "boolean" },
        "disabilityDegree":              { "type": "string", "enum": ["severe", "accentuated", "medium"] }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// insurance status, commission verification, and the recognized disability
    /// degree; the benefit is a tier-table lookup keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_WORK_ACCIDENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "DISABILITY_WORK_ACCIDENT_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3000.00,
          "accentuated": 2200.00,
          "medium":      1500.00
        }
      },
      "successCode": "DISABILITY_WORK_ACCIDENT_ELIGIBLE"
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
    /// var passport = DisabilityWorkAccidentPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie de dizabilitate (accident de muncă)",
            NameEn = "Work-accident disability pension",
            NameRu = "Пенсия по инвалидности (несчастный случай на производстве)",
            DescriptionRo =
                "Pensie acordată persoanelor cu dizabilitate cauzată de un accident de muncă " +
                "sau boală profesională verificată de comisia medicală, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-WORK-ACCIDENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
