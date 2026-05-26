using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-A — Ajutor de șomaj (Unemployment allowance) seed
/// row. Eligibility requires the claimant to be registered with SI AISSS (Sistemul
/// Informațional al Agenției Naționale pentru Ocuparea Forței de Muncă) and to
/// have more than 8 months of contribution stage; the benefit is 50% of the
/// last average salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-A. Bază normativă: Legea 105/2018 cu privire la promovarea
/// ocupării forței de muncă și asigurarea de șomaj.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact to be a <c>Money</c> value; <c>lastAverageSalaryMdl</c> is
/// therefore supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = UnemploymentAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class UnemploymentAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-A-UNEMPLOYMENT-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor de șomaj",
      "type": "object",
      "required": ["registeredAtSiAisss", "contributionMonths", "lastAverageSalaryMdl", "claimantIdnp"],
      "properties": {
        "registeredAtSiAisss":   { "type": "boolean" },
        "contributionMonths":    { "type": "integer", "minimum": 0 },
        "lastAverageSalaryMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// registration at SI AISSS and a contribution stage of more than 8 months
    /// (i.e. at least 9); the benefit is 50% of the last average salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "UNEMPLOYMENT_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "registeredAtSiAisss", "value": true,
          "failCode": "UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 8,
          "failCode": "UNEMPLOYMENT_ALLOWANCE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "lastAverageSalaryMdl"
      },
      "successCode": "UNEMPLOYMENT_ALLOWANCE_ELIGIBLE"
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
            NameRo = "Ajutor de șomaj",
            NameEn = "Unemployment allowance",
            NameRu = "Пособие по безработице",
            DescriptionRo =
                "Indemnizație lunară acordată persoanelor înregistrate ca șomeri la ANOFM și " +
                "care au realizat stagiu minim de cotizare, conform Legii 105/2018.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-UNEMPLOYMENT-ALLOWANCE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
