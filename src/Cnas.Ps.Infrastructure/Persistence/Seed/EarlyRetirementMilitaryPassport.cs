using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-C — Pensie anticipată pentru militari (Early-retirement
/// pension for military personnel) seed row. Eligibility requires confirmed military
/// personnel status and more than 19 years of effective service; the benefit is 50%
/// of the last military salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-C. Bază normativă: Legea 1544/1993 privind asigurarea cu pensii a
/// militarilor și a persoanelor din corpul de comandă și din trupele organelor
/// afacerilor interne. Valoarea procentuală (50%) este provizorie — de actualizat
/// după publicare HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastMilitarySalaryMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = EarlyRetirementMilitaryPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class EarlyRetirementMilitaryPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-C-EARLY-RETIREMENT-MILITARY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie anticipată pentru militari",
      "type": "object",
      "required": ["isMilitaryPersonnel", "serviceYears", "lastMilitarySalaryMdl", "claimantIdnp"],
      "properties": {
        "isMilitaryPersonnel":   { "type": "boolean" },
        "serviceYears":          { "type": "integer", "minimum": 0 },
        "lastMilitarySalaryMdl": { "type": "number",  "minimum": 0 },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// confirmed military-personnel status and a service stage of more than 19 years;
    /// the benefit is 50% of the last military salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "EARLY_RETIREMENT_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "EARLY_RETIREMENT_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-greater-than", "fact": "serviceYears", "value": 19,
          "failCode": "EARLY_RETIREMENT_MIL_INELIGIBLE_SERVICE_YEARS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "EARLY_RETIREMENT_MIL_ELIGIBLE"
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
            NameRo = "Pensie anticipată pentru militari",
            NameEn = "Early-retirement pension (military)",
            NameRu = "Досрочная пенсия для военнослужащих",
            DescriptionRo =
                "Pensie anticipată acordată militarilor cu mai mult de 19 ani de serviciu, " +
                "calculată ca 50% din ultima soldă militară, conform Legii 1544/1993.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-EARLY-RETIREMENT-MIL-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
