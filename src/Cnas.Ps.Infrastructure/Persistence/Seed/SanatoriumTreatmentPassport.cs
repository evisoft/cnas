using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.4-A — Tratament balneo-sanatorial
/// (Sanatorium / balneological treatment voucher) seed row. Eligibility requires
/// that the claimant is insured, holds a medical recommendation, and has not
/// received a voucher in the last two years. The payload is a fixed 0 MDL marker
/// because the benefit is delivered as a voucher rather than cash.
/// </summary>
/// <remarks>
/// TOR §3.4-A. The 2-year cooldown reflects the Government Decision currently in
/// force for sanatorium-voucher allocation; the rule is expressed as
/// <c>fact-greater-than 2</c> (i.e. strictly more than two years since the last
/// voucher), and the boolean medical-recommendation guard mirrors the standard
/// commission gate. All thresholds can be tuned via passport upsert.
/// </remarks>
/// <example>
/// <code>
/// var passport = SanatoriumTreatmentPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SanatoriumTreatmentPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.4-A-SANATORIUM";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Tratament balneo-sanatorial",
      "type": "object",
      "required": ["isInsured", "medicalRecommendationOnFile", "lastSanatoriumYears"],
      "properties": {
        "isInsured":                   { "type": "boolean" },
        "medicalRecommendationOnFile": { "type": "boolean" },
        "lastSanatoriumYears":         { "type": "integer", "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// insurance status, the presence of a medical recommendation, and that more
    /// than two years have passed since the last voucher; the payload is a fixed
    /// 0 MDL marker (voucher kind — no monetary payout).
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SANATORIUM",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "SANATORIUM_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "medicalRecommendationOnFile", "value": true,
          "failCode": "SANATORIUM_INELIGIBLE_NO_RECOMMENDATION" },
        { "rule": "fact-greater-than", "fact": "lastSanatoriumYears", "value": 2,
          "failCode": "SANATORIUM_INELIGIBLE_COOLDOWN" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "SANATORIUM_ELIGIBLE"
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
    /// var passport = SanatoriumTreatmentPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Tratament balneo-sanatorial",
            NameEn = "Sanatorium / balneological treatment voucher",
            NameRu = "Санаторно-курортное лечение",
            DescriptionRo =
                "Bilet de tratament balneo-sanatorial acordat persoanelor asigurate, " +
                "pe baza recomandării medicale și a perioadei de carantină de 2 ani.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SANATORIUM-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
