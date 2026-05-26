using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-A — Pensie pentru limită de vârstă (Old-age pension)
/// seed row. Eligibility requires the claimant to be at least the statutory
/// retirement age and to have accumulated the minimum contribution years
/// mandated by Legea 156/1998.
/// </summary>
/// <remarks>
/// The retirement age (63 years) and minimum contribution stage (34 years) reflect
/// the unified target values established by the 2017 reform. The 45% replacement
/// rate applied to the average insured income is a reasonable Moldovan default;
/// the actual indexed formula can be substituted later via passport upsert.
/// </remarks>
/// <example>
/// <code>
/// var passport = OldAgePensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class OldAgePensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-A-OLD-AGE-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru limită de vârstă",
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
    /// that the claimant has reached 63 years of age and has more than 33 years of
    /// contribution stage (i.e. at least 34); the benefit is 45% of the average
    /// insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "OLD_AGE_PENSION",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 63, "max": 120,
          "failCode": "OLD_AGE_PENSION_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 33,
          "failCode": "OLD_AGE_PENSION_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 45,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "OLD_AGE_PENSION_ELIGIBLE"
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
    /// var passport = OldAgePensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie pentru limită de vârstă",
            NameEn = "Old-age pension",
            NameRu = "Пенсия по возрасту",
            DescriptionRo =
                "Pensie acordată persoanelor care au atins vârsta standard de pensionare " +
                "și care au realizat stagiul minim de cotizare prevăzut de Legea 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-OLD-AGE-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
