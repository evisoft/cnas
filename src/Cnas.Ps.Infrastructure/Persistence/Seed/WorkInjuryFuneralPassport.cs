using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.11-C — Ajutor de deces (accident de muncă)
/// (Work-injury funeral allowance) seed row. Eligibility requires that the death
/// resulted from a workplace accident and that the claim is filed within 365 days
/// of the death; the benefit is a fixed amount paid to the family.
/// </summary>
/// <remarks>
/// <para>TOR §3.11-C. Bază normativă: Legea 156/1998, art. 22^1. The 5 000 MDL
/// fixed amount is a Moldovan default — valoare provizorie, de actualizat conform
/// indexării anuale.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WorkInjuryFuneralPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WorkInjuryFuneralPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.11-C-WORK-INJURY-FUNERAL";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor de deces (accident de muncă)",
      "type": "object",
      "required": ["deathFromWorkAccident", "dateOfDeathUtc", "claimDateUtc", "claimantIdnp"],
      "properties": {
        "deathFromWorkAccident": { "type": "boolean" },
        "dateOfDeathUtc":        { "type": "string", "format": "date-time" },
        "claimDateUtc":          { "type": "string", "format": "date-time" },
        "claimantIdnp":          { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the work-accident cause of death and that the claim is filed within 365
    /// days of the death; the benefit is a fixed 5 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WORK_INJURY_FUNERAL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deathFromWorkAccident", "value": true,
          "failCode": "WORK_INJURY_FUNERAL_INELIGIBLE_NOT_WORK_ACCIDENT" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "WORK_INJURY_FUNERAL_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 5000.00,
        "currency": "MDL"
      },
      "successCode": "WORK_INJURY_FUNERAL_ELIGIBLE"
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
            NameRo = "Ajutor de deces (accident de muncă)",
            NameEn = "Work-injury funeral allowance",
            NameRu = "Пособие на погребение (несчастный случай на производстве)",
            DescriptionRo =
                "Ajutor unic acordat familiei salariatului decedat în urma unui accident " +
                "de muncă, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WORK-INJURY-FUNERAL-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
