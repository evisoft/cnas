using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-B — Pensie anticipată pentru limită de vârstă
/// (Anticipated old-age pension) seed row. Eligibility requires that the claimant
/// is between 58 and 62 years old on the claim date and has accumulated more than
/// 36 years of contribution stage. The benefit is 40% of the average insured income.
/// </summary>
/// <remarks>
/// TOR §3.2-B. The 58–62 age window and the &gt; 36-year contribution requirement
/// mirror Legea 156/1998 art. 41<sup>2</sup>; the 40% replacement rate is a
/// reasonable Moldovan default and can be tuned via passport upsert without code
/// changes.
/// </remarks>
/// <example>
/// <code>
/// var passport = AnticipatedOldAgePensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class AnticipatedOldAgePensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-B-ANTICIPATED-OLD-AGE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie anticipată pentru limită de vârstă",
      "type": "object",
      "required": ["dobUtc", "claimDateUtc", "contributionYears", "averageInsuredIncomeMdl"],
      "properties": {
        "dobUtc":                  { "type": "string", "format": "date-time" },
        "claimDateUtc":            { "type": "string", "format": "date-time" },
        "contributionYears":       { "type": "integer", "minimum": 0 },
        "averageInsuredIncomeMdl": { "type": "number",  "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the claimant is between 58 and 62 years of age and has more than 36
    /// years of contribution stage; the benefit is 40% of the average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "ANTICIPATED_OLD_AGE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 58, "max": 62,
          "failCode": "ANTICIPATED_OLD_AGE_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 36,
          "failCode": "ANTICIPATED_OLD_AGE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 40,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "ANTICIPATED_OLD_AGE_ELIGIBLE"
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
    /// var passport = AnticipatedOldAgePensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie anticipată pentru limită de vârstă",
            NameEn = "Anticipated old-age pension",
            NameRu = "Досрочная пенсия по возрасту",
            DescriptionRo =
                "Pensie acordată persoanelor cu stagiu de cotizare ce depășește limita standard, " +
                "anticipând cu până la 5 ani vârsta de pensionare, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-ANTICIPATED-OLD-AGE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
