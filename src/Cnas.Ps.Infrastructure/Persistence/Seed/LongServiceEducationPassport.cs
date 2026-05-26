using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-A — Pensie pentru vechime în muncă (educație)
/// (Long-service pension for education-sector workers) seed row. Eligibility
/// requires the claimant worked as an educator for more than 24 years; the
/// benefit is 75% of the last salary in the education sector.
/// </summary>
/// <remarks>
/// <para>TOR §3.10-A. Bază normativă: Codul Educației al RM (Legea 152/2014) și
/// Legea 156/1998 privind sistemul public de pensii.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact (<c>lastEducatorSalaryMdl</c>) to be a <c>Money</c> value.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceEducationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceEducationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-A-LONG-SERVICE-EDUCATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (educație)",
      "type": "object",
      "required": ["wasEducator", "educationServiceYears", "lastEducatorSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasEducator":            { "type": "boolean" },
        "educationServiceYears":  { "type": "integer", "minimum": 0 },
        "lastEducatorSalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":           { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// educator status and a service stage of more than 24 years; the benefit is 75%
    /// of the last educator salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_EDU",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasEducator", "value": true,
          "failCode": "LONG_SERVICE_EDU_INELIGIBLE_NOT_EDUCATOR" },
        { "rule": "fact-greater-than", "fact": "educationServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_EDU_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "lastEducatorSalaryMdl"
      },
      "successCode": "LONG_SERVICE_EDU_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (educație)",
            NameEn = "Long-service pension (education)",
            NameRu = "Пенсия за выслугу лет (образование)",
            DescriptionRo =
                "Pensie lunară acordată cadrelor didactice cu vechime în muncă de peste 25 ani " +
                "în sistemul de educație, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-EDU-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
