using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-B — Pensie pentru vechime în muncă (ocrotirea
/// sănătății) (Long-service pension for healthcare workers) seed row.
/// Eligibility requires the claimant worked in healthcare for more than 24 years;
/// the benefit is 75% of the last salary in the healthcare sector.
/// </summary>
/// <remarks>
/// TOR §3.10-B. Bază normativă: Legea 411/1995 a ocrotirii sănătății și Legea
/// 156/1998 privind sistemul public de pensii. Engine note: the percent-of-fact
/// amount kind requires <c>lastHealthcareSalaryMdl</c> to be a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceHealthcarePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceHealthcarePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-B-LONG-SERVICE-HEALTHCARE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (sănătate)",
      "type": "object",
      "required": ["wasHealthcareWorker", "healthcareServiceYears", "lastHealthcareSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasHealthcareWorker":      { "type": "boolean" },
        "healthcareServiceYears":   { "type": "integer", "minimum": 0 },
        "lastHealthcareSalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":             { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// healthcare-sector worker status and a service stage of more than 24 years;
    /// the benefit is 75% of the last healthcare-sector salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_HEALTH",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasHealthcareWorker", "value": true,
          "failCode": "LONG_SERVICE_HEALTH_INELIGIBLE_NOT_HEALTHCARE" },
        { "rule": "fact-greater-than", "fact": "healthcareServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_HEALTH_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastHealthcareSalaryMdl"
      },
      "successCode": "LONG_SERVICE_HEALTH_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (sănătate)",
            NameEn = "Long-service pension (healthcare)",
            NameRu = "Пенсия за выслугу лет (здравоохранение)",
            DescriptionRo =
                "Pensie lunară acordată lucrătorilor din sistemul de ocrotire a sănătății " +
                "cu vechime în muncă de peste 25 ani, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-HEALTH-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
