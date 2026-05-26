using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-E — Pensie pentru vechime în muncă (transport
/// feroviar) (Long-service pension for railway workers) seed row. Eligibility
/// requires the claimant worked in railways for more than 24 years; the benefit
/// is 65% of the last railway-sector salary.
/// </summary>
/// <remarks>
/// TOR §3.10-E. Bază normativă: Legea 156/1998 privind sistemul public de pensii.
/// Engine note: <c>percent-of-fact</c> requires <c>lastRailwaySalaryMdl</c> to be
/// a <c>Money</c> value supplied by the caller.
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceRailwayPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceRailwayPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-E-LONG-SERVICE-RAILWAY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (transport feroviar)",
      "type": "object",
      "required": ["wasRailwayWorker", "railwayServiceYears", "lastRailwaySalaryMdl", "claimantIdnp"],
      "properties": {
        "wasRailwayWorker":      { "type": "boolean" },
        "railwayServiceYears":   { "type": "integer", "minimum": 0 },
        "lastRailwaySalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// railway-worker status and a service stage of more than 24 years; the benefit
    /// is 65% of the last railway salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_RAIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasRailwayWorker", "value": true,
          "failCode": "LONG_SERVICE_RAIL_INELIGIBLE_NOT_RAILWAY" },
        { "rule": "fact-greater-than", "fact": "railwayServiceYears", "value": 24,
          "failCode": "LONG_SERVICE_RAIL_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 65,
        "referenceFact": "lastRailwaySalaryMdl"
      },
      "successCode": "LONG_SERVICE_RAIL_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (transport feroviar)",
            NameEn = "Long-service pension (railway)",
            NameRu = "Пенсия за выслугу лет (железная дорога)",
            DescriptionRo =
                "Pensie lunară acordată lucrătorilor din transportul feroviar cu vechime " +
                "în muncă de peste 25 ani, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-RAIL-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
