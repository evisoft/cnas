using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-C — Pensie parțială pentru limită de vârstă
/// (Partial old-age pension) seed row. Eligibility requires that the claimant has
/// reached the standard retirement age (63+) and has accumulated between 15 and 33
/// years of contribution stage (i.e. enough to qualify for some benefit but not
/// enough for a full pension). The benefit is 25% of the average insured income.
/// </summary>
/// <remarks>
/// <para>
/// TOR §3.2-C. The 15-year minimum and the 34-year cap on contribution stage are
/// expressed as two independent rules — <c>fact-greater-than 14</c> (i.e. ≥ 15) and
/// <c>fact-less-than 34</c> — so that the engine can emit distinct fail codes for
/// each side of the band ("contributions too low" vs "qualifies for full pension").
/// </para>
/// <para>
/// The 25% reduced replacement rate is a reasonable Moldovan default for the
/// partial pension tier and can be tuned via passport upsert without code changes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var passport = PartialOldAgePensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class PartialOldAgePensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-C-PARTIAL-OLD-AGE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie parțială pentru limită de vârstă",
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
    /// the standard retirement age (63+) and a contribution band of 15..33 years
    /// inclusive; the benefit is 25% of the average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PARTIAL_OLD_AGE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 63, "max": 120,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_AGE" },
        { "rule": "fact-greater-than", "fact": "contributionYears", "value": 14,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_LOW" },
        { "rule": "fact-less-than", "fact": "contributionYears", "value": 34,
          "failCode": "PARTIAL_OLD_AGE_INELIGIBLE_CONTRIBUTIONS_FULL" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 25,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "PARTIAL_OLD_AGE_ELIGIBLE"
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
    /// var passport = PartialOldAgePensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie parțială pentru limită de vârstă",
            NameEn = "Partial old-age pension",
            NameRu = "Частичная пенсия по возрасту",
            DescriptionRo =
                "Pensie redusă acordată persoanelor care au atins vârsta standard de pensionare " +
                "dar nu au realizat stagiul complet de cotizare, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PARTIAL-OLD-AGE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
