using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.10-D — Pensie pentru vechime în muncă (minerit)
/// (Long-service pension for mining workers) seed row. Eligibility requires the
/// claimant worked as a miner for more than 19 years; the benefit is 70% of the
/// last mining-sector salary.
/// </summary>
/// <remarks>
/// TOR §3.10-D. Bază normativă: Legea 156/1998 privind sistemul public de pensii.
/// Engine note: the percent-of-fact amount kind requires <c>lastMiningSalaryMdl</c>
/// to be a <c>Money</c> value supplied by the caller.
/// </remarks>
/// <example>
/// <code>
/// var passport = LongServiceMiningPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class LongServiceMiningPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.10-D-LONG-SERVICE-MINING";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru vechime în muncă (minerit)",
      "type": "object",
      "required": ["wasMiner", "miningServiceYears", "lastMiningSalaryMdl", "claimantIdnp"],
      "properties": {
        "wasMiner":             { "type": "boolean" },
        "miningServiceYears":   { "type": "integer", "minimum": 0 },
        "lastMiningSalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// miner status and a service stage of more than 19 years; the benefit is 70%
    /// of the last mining salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "LONG_SERVICE_MINE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasMiner", "value": true,
          "failCode": "LONG_SERVICE_MINE_INELIGIBLE_NOT_MINER" },
        { "rule": "fact-greater-than", "fact": "miningServiceYears", "value": 19,
          "failCode": "LONG_SERVICE_MINE_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "lastMiningSalaryMdl"
      },
      "successCode": "LONG_SERVICE_MINE_ELIGIBLE"
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
            NameRo = "Pensie pentru vechime în muncă (minerit)",
            NameEn = "Long-service pension (mining)",
            NameRu = "Пенсия за выслугу лет (горнодобывающая)",
            DescriptionRo =
                "Pensie lunară acordată minerilor cu vechime în muncă de peste 20 ani, " +
                "conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-LONG-SERVICE-MINE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
