using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.1-D — Indemnizație pentru îngrijirea copilului bolnav
/// (Sick-leave-for-sick-child allowance) seed row. Eligibility requires that the
/// claimant is insured and that the sick child is younger than 15 years on the
/// claim date. The benefit is 90% of the parent's reference daily salary.
/// </summary>
/// <remarks>
/// <para>
/// TOR §3.1-D. The 90% replacement rate and the 14-year child-age cap mirror the
/// rules in Legea 289/2004 and HG 108/2005 for parent-administered sick care; the
/// concrete numeric defaults can be tuned via passport upsert without code changes.
/// </para>
/// <para>
/// Note that <c>fact-less-than</c> is strict, so a child aged exactly 14 still passes
/// against the threshold value of 15 (i.e. the rule reads "child age in years &lt; 15"
/// which excludes 15-year-olds and older).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var passport = SickLeaveChildPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SickLeaveChildPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-D-SICK-LEAVE-CHILD";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru îngrijirea copilului bolnav",
      "type": "object",
      "required": ["isInsured", "childAgeYears", "dailySalaryMdl"],
      "properties": {
        "isInsured":      { "type": "boolean" },
        "childAgeYears":  { "type": "integer", "minimum": 0 },
        "dailySalaryMdl": { "type": "number",  "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the parent is insured and the sick child is younger than 15 years; the
    /// benefit is 90% of the reference daily salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SICK_LEAVE_CHILD",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "SICK_LEAVE_CHILD_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-less-than", "fact": "childAgeYears", "value": 15,
          "failCode": "SICK_LEAVE_CHILD_INELIGIBLE_CHILD_TOO_OLD" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 90,
        "referenceFact": "dailySalaryMdl"
      },
      "successCode": "SICK_LEAVE_CHILD_ELIGIBLE"
    }
    """;

    /// <summary>
    /// Builds a fully-populated <see cref="ServicePassport"/> seed row stamped with
    /// the supplied clock's <c>UtcNow</c>.
    /// </summary>
    /// <param name="clock">Clock abstraction (UTC) used to stamp <c>CreatedAtUtc</c>.</param>
    /// <returns>A new <see cref="ServicePassport"/> ready to be inserted into the DB.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
    /// <example>
    /// <code>
    /// var passport = SickLeaveChildPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Indemnizație pentru îngrijirea copilului bolnav",
            NameEn = "Sick-leave allowance for child care",
            NameRu = "Пособие по уходу за больным ребенком",
            DescriptionRo =
                "Indemnizație plătită părintelui asigurat care îngrijește copilul bolnav " +
                "în vârstă de până la 14 ani, conform Legii 289/2004 și HG 108/2005.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SICK-LEAVE-CHILD-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
