using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.13-B — Îngrijire la domiciliu (vârstnici)
/// (Domiciliary elderly-care subsidy) seed row. Eligibility requires that the
/// claimant needs domiciliary care and is older than 69; the benefit is 50% of
/// the reference care cost.
/// </summary>
/// <remarks>
/// TOR §3.13-B. Bază normativă: Legea 60/2012 și HG privind serviciile sociale
/// de îngrijire la domiciliu. Engine note: the percent-of-fact amount kind
/// requires <c>referenceCareCostMdl</c> to be supplied as a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = DomiciliaryElderlyCarePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DomiciliaryElderlyCarePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.13-B-DOMICILIARY-ELDERLY-CARE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Îngrijire la domiciliu (vârstnici)",
      "type": "object",
      "required": ["requiresDomiciliaryCare", "elderlyAgeYears", "referenceCareCostMdl", "claimantIdnp"],
      "properties": {
        "requiresDomiciliaryCare":  { "type": "boolean" },
        "elderlyAgeYears":          { "type": "integer", "minimum": 0 },
        "referenceCareCostMdl":     { "type": "number",  "minimum": 0 },
        "claimantIdnp":             { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that domiciliary care is required and the claimant age &gt; 69; the benefit
    /// is 50% of the reference care cost.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DOMICILIARY_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "requiresDomiciliaryCare", "value": true,
          "failCode": "DOMICILIARY_CARE_INELIGIBLE_NOT_REQUIRED" },
        { "rule": "fact-greater-than", "fact": "elderlyAgeYears", "value": 69,
          "failCode": "DOMICILIARY_CARE_INELIGIBLE_TOO_YOUNG" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 50,
        "referenceFact": "referenceCareCostMdl"
      },
      "successCode": "DOMICILIARY_CARE_ELIGIBLE"
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
            NameRo = "Îngrijire la domiciliu (vârstnici)",
            NameEn = "Domiciliary elderly-care subsidy",
            NameRu = "Уход на дому (пожилые)",
            DescriptionRo =
                "Subvenție pentru servicii de îngrijire la domiciliu acordată persoanelor " +
                "vârstnice ce necesită îngrijire continuă.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DOMICILIARY-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
