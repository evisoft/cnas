using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.12-C — Alocație pentru părinte unic
/// (Single-parent allowance) seed row. Eligibility requires the claimant to be a
/// single parent with at least one dependent child; the benefit is a fixed
/// monthly amount.
/// </summary>
/// <remarks>
/// TOR §3.12-C. Bază normativă: Legea 499/1999 privind alocațiile sociale de
/// stat. The 1 200 MDL fixed amount is a Moldovan default — valoare provizorie,
/// de actualizat la indexarea anuală.
/// </remarks>
/// <example>
/// <code>
/// var passport = SingleParentAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SingleParentAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.12-C-SINGLE-PARENT-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru părinte unic",
      "type": "object",
      "required": ["isSingleParent", "dependentChildrenCount", "claimantIdnp"],
      "properties": {
        "isSingleParent":          { "type": "boolean" },
        "dependentChildrenCount":  { "type": "integer", "minimum": 0 },
        "claimantIdnp":            { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// single-parent status and at least one dependent child; the benefit is a
    /// fixed 1 200 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SINGLE_PARENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isSingleParent", "value": true,
          "failCode": "SINGLE_PARENT_INELIGIBLE_NOT_SINGLE_PARENT" },
        { "rule": "fact-greater-than", "fact": "dependentChildrenCount", "value": 0,
          "failCode": "SINGLE_PARENT_INELIGIBLE_NO_DEPENDENTS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1200.00,
        "currency": "MDL"
      },
      "successCode": "SINGLE_PARENT_ELIGIBLE"
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
            NameRo = "Alocație pentru părinte unic",
            NameEn = "Single-parent allowance",
            NameRu = "Пособие одинокому родителю",
            DescriptionRo =
                "Alocație lunară acordată părinților unici cu cel puțin un copil dependent, " +
                "conform Legii 499/1999.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SINGLE-PARENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
