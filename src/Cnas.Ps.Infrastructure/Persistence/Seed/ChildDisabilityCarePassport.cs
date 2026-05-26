using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-D — Indemnizație pentru îngrijirea copilului cu
/// dizabilitate (Child-disability care allowance) seed row. Eligibility requires
/// the child to have a recognized severe or accentuated disability and to be a
/// minor (under 18); the benefit is tiered by disability severity.
/// </summary>
/// <remarks>
/// <para>TOR §3.3-D. Bază normativă: Legea 499/1999 privind alocațiile sociale
/// de stat și HG-urile anuale de indexare.</para>
/// <para>Tier values (1 800 / 1 400 MDL) are reasonable Moldovan defaults derived
/// from the task brief and can be tuned via passport upsert.</para>
/// <para>Engine note: the spec calls for <c>childAgeYears ≤ 17</c>. Because the
/// engine's <c>fact-less-than</c> is strict (<c>&lt;</c>), the equivalent check
/// <c>childAgeYears &lt; 18</c> is used here, which yields identical results for
/// integer ages.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ChildDisabilityCarePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ChildDisabilityCarePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-D-CHILD-DISABILITY-CARE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru îngrijirea copilului cu dizabilitate",
      "type": "object",
      "required": ["childDisabilityDegree", "childAgeYears", "caregiverIdnp"],
      "properties": {
        "childDisabilityDegree": { "type": "string",  "enum": ["severe", "accentuated"] },
        "childAgeYears":         { "type": "integer", "minimum": 0, "maximum": 18 },
        "caregiverIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// a recognized child disability degree and that the child is still a minor;
    /// the benefit is looked up in a tier table keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "CHILD_DISABILITY_CARE",
      "eligibility": [
        { "rule": "fact-in-set", "fact": "childDisabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "CHILD_DISABILITY_CARE_INELIGIBLE_DEGREE" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 18,
          "failCode": "CHILD_DISABILITY_CARE_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "childDisabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      1800.00,
          "accentuated": 1400.00
        }
      },
      "successCode": "CHILD_DISABILITY_CARE_ELIGIBLE"
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
            NameRo = "Indemnizație pentru îngrijirea copilului cu dizabilitate",
            NameEn = "Child-disability care allowance",
            NameRu = "Пособие по уходу за ребенком-инвалидом",
            DescriptionRo =
                "Indemnizație lunară acordată părintelui sau tutorelui care îngrijește un copil " +
                "cu dizabilitate severă sau accentuată, conform Legii 499/1999.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-CHILD-DISABILITY-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
