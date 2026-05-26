using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.15-C — Pensie pentru savant
/// (Scientist pension) seed row. Eligibility requires the claimant to hold a
/// scientific degree and to have more than 24 years of research career;
/// the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.15-C. Bază normativă: Codul cu privire la știință și inovare
/// (Legea 259/2017) și Legea 156/1998 art. 47. The 4 500 MDL fixed amount is a
/// Moldovan default — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ScientistPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ScientistPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.15-C-SCIENTIST-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie savant",
      "type": "object",
      "required": ["holdsScientificDegree", "researchCareerYears", "claimantIdnp"],
      "properties": {
        "holdsScientificDegree": { "type": "boolean" },
        "researchCareerYears":   { "type": "integer", "minimum": 0 },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// scientific degree and a research career &gt; 24 years; the benefit is a
    /// fixed 4 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SCIENTIST_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "holdsScientificDegree", "value": true,
          "failCode": "SCIENTIST_PENSION_INELIGIBLE_NO_DEGREE" },
        { "rule": "fact-greater-than", "fact": "researchCareerYears", "value": 24,
          "failCode": "SCIENTIST_PENSION_INELIGIBLE_CAREER_YEARS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 4500.00,
        "currency": "MDL"
      },
      "successCode": "SCIENTIST_PENSION_ELIGIBLE"
    }
    """;

    /// <summary>
    /// Builds a fully-populated <see cref="ServicePassport"/> seed row stamped with
    /// the supplied clock's <c>UtcNow</c>.
    /// </summary>
    /// <param name="clock">Clock abstraction (UTC) used to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>A new <see cref="ServicePassport"/> ready to be inserted into the DB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie savant",
            NameEn = "Scientist pension",
            NameRu = "Пенсия учёного",
            DescriptionRo =
                "Pensie lunară acordată savanților cu grad științific și carieră de cercetare " +
                "de peste 25 ani, conform Legii 259/2017 și Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SCIENTIST-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
