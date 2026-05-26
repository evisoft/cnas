using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-H — Indemnizație lunară pentru invalizii de război
/// (War-invalid monthly allowance) seed row. Eligibility requires recognized war-
/// veteran status and a registered disability degree; the benefit is tiered by
/// severity (severe / accentuated / medium).
/// </summary>
/// <remarks>
/// <para>TOR §3.8-H. Bază normativă: Legea 190/2003 privind veteranii și HG-urile
/// anuale de indexare. Valorile tier (4 000 / 3 000 / 2 200 MDL) sunt provizorii —
/// de actualizat după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WarInvalidMonthlyAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WarInvalidMonthlyAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-H-WAR-INVALID-MONTHLY-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație lunară pentru invalizii de război",
      "type": "object",
      "required": ["isWarVeteran", "disabilityDegree", "claimantIdnp"],
      "properties": {
        "isWarVeteran":     { "type": "boolean" },
        "disabilityDegree": { "type": "string", "enum": ["severe", "accentuated", "medium"] },
        "claimantIdnp":     { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// veteran status and a recognized disability degree; the benefit is looked up
    /// in a tier table keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WAR_INVALID_MONTHLY",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isWarVeteran", "value": true,
          "failCode": "WAR_INVALID_MONTHLY_INELIGIBLE_NOT_VETERAN" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated", "medium"],
          "failCode": "WAR_INVALID_MONTHLY_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      4000.00,
          "accentuated": 3000.00,
          "medium":      2200.00
        }
      },
      "successCode": "WAR_INVALID_MONTHLY_ELIGIBLE"
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
            NameRo = "Indemnizație lunară pentru invalizii de război",
            NameEn = "War-invalid monthly allowance",
            NameRu = "Ежемесячное пособие инвалидам войны",
            DescriptionRo =
                "Indemnizație lunară acordată veteranilor de război cu dizabilitate, " +
                "diferențiată pe grade de severitate, conform Legii 190/2003.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WAR-INVALID-MONTHLY-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
