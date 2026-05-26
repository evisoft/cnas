using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-C — Pensie pentru vechime în muncă (agricultură)
/// (Long-service pension for agricultural workers) seed row. Eligibility requires
/// the claimant worked in agriculture for more than 29 years; the benefit is 60%
/// of the last agricultural-sector salary.
/// </summary>
/// <remarks>
/// TOR §3.10-C. Bază normativă: Legea 156/1998 privind sistemul public de pensii.
/// Engine note: the percent-of-fact amount kind requires <c>lastAgriSalaryMdl</c>
/// to be a <c>Money</c> value supplied by the caller.
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceAgriculturePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceAgriculturePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-C-LONG-SERVICE-AGRICULTURE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (agricultură)",
      "type": "object",
      "required": ["wasAgriculturalWorker", "agriculturalServiceYears", "lastAgriSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasAgriculturalWorker":     { "type": "boolean" },
        "agriculturalServiceYears":  { "type": "integer", "minimum": 0 },
        "lastAgriSalaryMdl":         { "type": "number",  "minimum": 0 },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// agricultural-worker status and a service stage of more than 29 years; the
    /// benefit is 60% of the last agricultural salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_AGRI",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasAgriculturalWorker", "value": true,
          "failCode": "LONG_SERVICE_AGRI_INELIGIBLE_NOT_AGRI" },
        { "rule": "fact-greater-than", "fact": "agriculturalServiceYears", "value": 29,
          "failCode": "LONG_SERVICE_AGRI_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 60,
        "referenceFact": "lastAgriSalaryMdl"
      },
      "successCode": "LONG_SERVICE_AGRI_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (agricultură)",
            NameEn = "Long-service pension (agriculture)",
            NameRu = "Пенсия за выслугу лет (сельское хозяйство)",
            DescriptionRo =
                "Pensie lunară acordată lucrătorilor din agricultură cu vechime în muncă " +
                "de peste 30 ani, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-AGRI-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
