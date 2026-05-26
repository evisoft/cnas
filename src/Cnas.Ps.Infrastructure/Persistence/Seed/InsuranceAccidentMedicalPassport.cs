using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.18-A — Asigurare accident (rambursare cheltuieli
/// medicale) (Accident insurance — medical-cost reimbursement) seed row.
/// Eligibility requires the claimant to be insured and to have reported the
/// accident within 30 days; the benefit is 100% of the medical costs incurred.
/// </summary>
/// <remarks>
/// TOR §3.18-A. Bază normativă: Legea 289/2004 și HG privind asigurarea de
/// accident. Engine note: the percent-of-fact amount kind requires
/// <c>medicalCostsMdl</c> to be supplied as a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = InsuranceAccidentMedicalPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class InsuranceAccidentMedicalPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.18-A-INSURANCE-ACCIDENT-MEDICAL";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Asigurare accident (rambursare medical)",
      "type": "object",
      "required": ["isInsured", "accidentReportedWithin30Days", "medicalCostsMdl", "claimantIdnp"],
      "properties": {
        "isInsured":                     { "type": "boolean" },
        "accidentReportedWithin30Days":  { "type": "boolean" },
        "medicalCostsMdl":               { "type": "number", "minimum": 0 },
        "claimantIdnp":                  { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// insured status and timely accident reporting (within 30 days); the benefit
    /// is 100% of the medical costs incurred.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "INS_ACCIDENT_MED",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "INS_ACCIDENT_MED_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "accidentReportedWithin30Days", "value": true,
          "failCode": "INS_ACCIDENT_MED_INELIGIBLE_LATE_REPORT" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "medicalCostsMdl"
      },
      "successCode": "INS_ACCIDENT_MED_ELIGIBLE"
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
            NameRo = "Asigurare accident (rambursare medical)",
            NameEn = "Accident insurance medical reimbursement",
            NameRu = "Страхование от несчастных случаев (медрасходы)",
            DescriptionRo =
                "Rambursarea integrală a cheltuielilor medicale ale persoanei asigurate care a " +
                "raportat accidentul în primele 30 de zile.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-INS-ACCIDENT-MED-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
