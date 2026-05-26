using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-A — Pensie de dizabilitate (Disability pension)
/// seed row. Eligibility requires a recognized disability degree (severe,
/// accentuated or medium) and at least 12 months of contribution stage; the
/// benefit amount is tiered by disability degree.
/// </summary>
/// <remarks>
/// The fixed-amount table (2 500 / 1 800 / 1 200 MDL per degree) is a reasonable
/// Moldovan default ordered by severity and can be tuned later via passport
/// upsert when indexation rules change.
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-A-DISABILITY-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de dizabilitate",
      "type": "object",
      "required": ["disabilityDegree", "contributionMonths", "claimantIdnp"],
      "properties": {
        "disabilityDegree":   { "type": "string", "enum": ["severe", "accentuated", "medium"] },
        "contributionMonths": { "type": "integer", "minimum": 0 },
        "claimantIdnp":       { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the disability degree is one of the recognized values and that the
    /// claimant has more than 11 months of contribution (i.e. at least 12); the
    /// benefit is looked up in a tier table keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_PENSION",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "DISABILITY_PENSION_INELIGIBLE_DEGREE" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 11,
          "failCode": "DISABILITY_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      2500.00,
          "accentuated": 1800.00,
          "medium":      1200.00
        }
      },
      "successCode": "DISABILITY_PENSION_ELIGIBLE"
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
    /// var passport = DisabilityPensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie de dizabilitate",
            NameEn = "Disability pension",
            NameRu = "Пенсия по инвалидности",
            DescriptionRo =
                "Pensie acordată persoanelor cu dizabilitate severă, accentuată sau medie " +
                "care au realizat stagiul minim de cotizare, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
