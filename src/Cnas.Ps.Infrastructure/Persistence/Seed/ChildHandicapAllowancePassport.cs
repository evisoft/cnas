using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-A — Alocație pentru copii cu dizabilitate (Child-
/// handicap allowance) seed row. Eligibility requires a registered child disability
/// and the child to be a minor (under 18); the benefit is tiered by severity.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-A. Bază normativă: Legea 499/1999 privind alocațiile sociale de
/// stat. Valorile tier (2 000 / 1 500 / 1 000 MDL) sunt provizorii — de actualizat
/// după publicare HG/Lege.</para>
/// <para>Engine note: the spec calls for <c>childAgeYears &lt;= 17</c>; because the
/// engine's <c>fact-less-than</c> is strict, the equivalent
/// <c>childAgeYears &lt; 18</c> is used (identical for integer ages).</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ChildHandicapAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChildHandicapAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-A-CHILD-HANDICAP-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru copii cu dizabilitate",
      "type": "object",
      "required": ["childDisabilityRegistered", "childDisabilityDegree", "childAgeYears", "claimantIdnp"],
      "properties": {
        "childDisabilityRegistered": { "type": "boolean" },
        "childDisabilityDegree":     { "type": "string",  "enum": ["severe", "accentuated", "medium"] },
        "childAgeYears":             { "type": "integer", "minimum": 0, "maximum": 18 },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// disability registration and that the child is still a minor; the benefit is
    /// looked up in a tier table keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHILD_HANDICAP",
      "eligibility": [
        { "rule": "fact-equals", "fact": "childDisabilityRegistered", "value": true,
          "failCode": "CHILD_HANDICAP_INELIGIBLE_NOT_REGISTERED" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 18,
          "failCode": "CHILD_HANDICAP_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "childDisabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      2000.00,
          "accentuated": 1500.00,
          "medium":      1000.00
        }
      },
      "successCode": "CHILD_HANDICAP_ELIGIBLE"
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
            NameRo = "Alocație pentru copii cu dizabilitate",
            NameEn = "Child-handicap allowance",
            NameRu = "Пособие на ребёнка-инвалида",
            DescriptionRo =
                "Alocație lunară de stat acordată copiilor cu dizabilitate înregistrată, " +
                "diferențiată pe grade de severitate, conform Legii 499/1999.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHILD-HANDICAP-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
