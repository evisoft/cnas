using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-C — Pensie de urmaș pentru deces din accident de
/// muncă (Death-from-work-accident survivor pension) seed row. Eligibility
/// requires that the fatal accident has been verified by the medical commission
/// and that the survivor is in a recognized relationship to the deceased.
/// The benefit is 100% of the deceased's average insured income.
/// </summary>
/// <remarks>
/// TOR §3.5-C. The 100% replacement rate distinguishes this from the standard
/// survivor pension (Annex 3.5-A, which pays 75%) and reflects the additional
/// employer-liability dimension when death is caused by a verified work accident,
/// per Legea 156/1998 art. 48. The rate can be tuned via passport upsert.
/// </remarks>
/// <example>
/// <code>
/// var passport = DeathFromWorkAccidentPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DeathFromWorkAccidentPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-C-DEATH-WORK-ACCIDENT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de urmaș (deces din accident de muncă)",
      "type": "object",
      "required": ["accidentVerifiedByCommission", "relationshipToDeceased", "deceasedAverageInsuredIncomeMdl"],
      "properties": {
        "accidentVerifiedByCommission":      { "type": "boolean" },
        "relationshipToDeceased":            { "type": "string", "enum": ["spouse", "child", "parent"] },
        "deceasedAverageInsuredIncomeMdl":   { "type": "number",  "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the fatal accident has been verified by the commission and that the
    /// survivor is in a recognized relationship; the benefit is 100% of the
    /// deceased's average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DEATH_WORK_ACCIDENT_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "DEATH_WORK_ACCIDENT_PENSION_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "DEATH_WORK_ACCIDENT_PENSION_ELIGIBLE"
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
    /// var passport = DeathFromWorkAccidentPensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie de urmaș (deces din accident de muncă)",
            NameEn = "Survivor pension (death from work accident)",
            NameRu = "Пенсия по случаю потери кормильца (несчастный случай на производстве)",
            DescriptionRo =
                "Pensie de urmaș acordată în cuantum majorat atunci când decesul susținătorului " +
                "a fost cauzat de un accident de muncă verificat de comisie, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DEATH-WORK-ACCIDENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
