using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.11-A — Indemnizație pentru accident de muncă
/// (Work-injury compensation) seed row. Eligibility requires that the workplace
/// accident has been verified by the medical commission and that the resulting
/// incapacity exceeds 30 days; the benefit is 100% of the average daily earnings.
/// </summary>
/// <remarks>
/// TOR §3.11-A. Bază normativă: Legea 289/2004 privind indemnizațiile pentru
/// incapacitate temporară de muncă. Engine note: <c>percent-of-fact</c> requires
/// <c>averageDailyEarningsMdl</c> to be supplied as a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = WorkInjuryCompensationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WorkInjuryCompensationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.11-A-WORK-INJURY-COMPENSATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru accident de muncă",
      "type": "object",
      "required": ["accidentVerifiedByCommission", "incapacityDays", "averageDailyEarningsMdl", "claimantIdnp"],
      "properties": {
        "accidentVerifiedByCommission": { "type": "boolean" },
        "incapacityDays":               { "type": "integer", "minimum": 0 },
        "averageDailyEarningsMdl":      { "type": "number",  "minimum": 0 },
        "claimantIdnp":                 { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// commission verification of the accident and an incapacity period exceeding
    /// 30 days; the benefit is 100% of the average daily earnings.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WORK_INJURY_COMP",
      "eligibility": [
        { "rule": "fact-equals", "fact": "accidentVerifiedByCommission", "value": true,
          "failCode": "WORK_INJURY_COMP_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-greater-than", "fact": "incapacityDays", "value": 30,
          "failCode": "WORK_INJURY_COMP_INELIGIBLE_INCAPACITY_TOO_SHORT" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "averageDailyEarningsMdl"
      },
      "successCode": "WORK_INJURY_COMP_ELIGIBLE"
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
            NameRo = "Indemnizație pentru accident de muncă",
            NameEn = "Work-injury compensation",
            NameRu = "Пособие при несчастном случае на производстве",
            DescriptionRo =
                "Indemnizație acordată salariaților care au suferit un accident de muncă " +
                "verificat de comisie, cu incapacitate de muncă de peste 30 zile.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WORK-INJURY-COMP-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
