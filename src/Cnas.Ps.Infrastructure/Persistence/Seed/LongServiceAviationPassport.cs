using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-F — Pensie pentru vechime în muncă (aviație)
/// (Long-service pension for aviation crew) seed row. Eligibility requires the
/// claimant served as flight crew for more than 5 999 flight-hours; the benefit
/// is 80% of the last aviation-sector salary.
/// </summary>
/// <remarks>
/// TOR §3.10-F. Bază normativă: Legea 156/1998 privind sistemul public de pensii.
/// Engine note: the threshold is expressed in flight-hours (a numeric fact) and
/// <c>percent-of-fact</c> requires <c>lastAviationSalaryMdl</c> as <c>Money</c>.
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceAviationPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceAviationPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-F-LONG-SERVICE-AVIATION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (aviație)",
      "type": "object",
      "required": ["wasAviationCrew", "flightHours", "lastAviationSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasAviationCrew":         { "type": "boolean" },
        "flightHours":             { "type": "integer", "minimum": 0 },
        "lastAviationSalaryMdl":   { "type": "number",  "minimum": 0 },
        "claimantIdnp":            { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// aviation-crew status and a flight stage of more than 5 999 hours; the
    /// benefit is 80% of the last aviation salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_AVI",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasAviationCrew", "value": true,
          "failCode": "LONG_SERVICE_AVI_INELIGIBLE_NOT_CREW" },
        { "rule": "fact-greater-than", "fact": "flightHours", "value": 5999,
          "failCode": "LONG_SERVICE_AVI_INELIGIBLE_FLIGHT_HOURS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 80,
        "referenceFact": "lastAviationSalaryMdl"
      },
      "successCode": "LONG_SERVICE_AVI_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (aviație)",
            NameEn = "Long-service pension (aviation)",
            NameRu = "Пенсия за выслугу лет (авиация)",
            DescriptionRo =
                "Pensie lunară acordată membrilor echipajului de zbor cu vechime de peste " +
                "6 000 ore de zbor, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-AVI-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
