using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.11-D — Pensie lunară de urmaș (accident de muncă)
/// (Work-injury monthly survivor pension) seed row. Eligibility requires that
/// the death resulted from a workplace accident and that the claimant is a
/// recognized survivor (spouse, child, or parent); the benefit is 100% of the
/// deceased's average insured income.
/// </summary>
/// <remarks>
/// TOR §3.11-D. Bază normativă: Legea 156/1998 art. 22. Engine note: the
/// percent-of-fact amount kind requires <c>deceasedAverageInsuredIncomeMdl</c>
/// to be supplied as a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = WorkInjurySurvivorMonthlyPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WorkInjurySurvivorMonthlyPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.11-D-WORK-INJURY-SURVIVOR-MONTHLY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie lunară de urmaș (accident de muncă)",
      "type": "object",
      "required": ["deathFromWorkAccident", "relationshipToDeceased", "deceasedAverageInsuredIncomeMdl", "claimantIdnp"],
      "properties": {
        "deathFromWorkAccident":          { "type": "boolean" },
        "relationshipToDeceased":         { "type": "string", "enum": ["spouse", "child", "parent"] },
        "deceasedAverageInsuredIncomeMdl":{ "type": "number", "minimum": 0 },
        "claimantIdnp":                   { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the work-accident cause of death and that the claimant is in the recognized
    /// survivor set; the benefit is 100% of the deceased's average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WORK_INJURY_SURVIVOR",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deathFromWorkAccident", "value": true,
          "failCode": "WORK_INJURY_SURVIVOR_INELIGIBLE_NOT_WORK_ACCIDENT" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "WORK_INJURY_SURVIVOR_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "WORK_INJURY_SURVIVOR_ELIGIBLE"
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
            NameRo = "Pensie lunară de urmaș (accident de muncă)",
            NameEn = "Work-injury monthly survivor pension",
            NameRu = "Ежемесячная пенсия по случаю потери кормильца (производство)",
            DescriptionRo =
                "Pensie lunară acordată urmașilor (soț/soție, copii, părinți) ai persoanei " +
                "decedate în urma unui accident de muncă, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WORK-INJURY-SURVIVOR-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
