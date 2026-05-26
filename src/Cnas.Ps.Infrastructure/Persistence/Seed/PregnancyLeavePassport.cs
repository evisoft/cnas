using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.1-E — Indemnizație de maternitate (sarcină și lăuzie)
/// (Pregnancy and maternity-leave allowance) seed row. Eligibility requires that
/// the claimant is insured and has accumulated at least 6 months of contribution.
/// The benefit is 100% of the average insured income.
/// </summary>
/// <remarks>
/// TOR §3.1-E. The 6-month minimum contribution and the 100% replacement rate
/// mirror Legea 289/2004 art. 7. The contribution rule is expressed as
/// <c>fact-greater-than 5</c> because the engine's comparator is strict ("greater
/// than 5" admits 6 and above). All numeric defaults can be tuned via passport
/// upsert without code changes.
/// </remarks>
/// <example>
/// <code>
/// var passport = PregnancyLeavePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class PregnancyLeavePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.1-E-PREGNANCY-LEAVE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație de maternitate (sarcină și lăuzie)",
      "type": "object",
      "required": ["isInsured", "contributionMonths", "averageInsuredIncomeMdl"],
      "properties": {
        "isInsured":                 { "type": "boolean" },
        "contributionMonths":        { "type": "integer", "minimum": 0 },
        "averageInsuredIncomeMdl":   { "type": "number",  "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the claimant is insured and has more than 5 months of contribution
    /// stage (i.e. at least 6); the benefit is 100% of the average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PREGNANCY_LEAVE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "PREGNANCY_LEAVE_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-greater-than", "fact": "contributionMonths", "value": 5,
          "failCode": "PREGNANCY_LEAVE_INELIGIBLE_CONTRIBUTIONS" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "averageInsuredIncomeMdl"
      },
      "successCode": "PREGNANCY_LEAVE_ELIGIBLE"
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
    /// var passport = PregnancyLeavePassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Indemnizație de maternitate (sarcină și lăuzie)",
            NameEn = "Pregnancy and maternity-leave allowance",
            NameRu = "Пособие по беременности и родам",
            DescriptionRo =
                "Indemnizație acordată femeii asigurate pe durata concediului de maternitate, " +
                "în cuantum egal cu venitul mediu lunar asigurat, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PREGNANCY-LEAVE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
