using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.14-B — Asigurare de dizabilitate pentru militari
/// (Military disability insurance) seed row. Eligibility requires that the
/// claimant is military personnel and that the disability has been verified as
/// arising from service; the benefit is 100% of the last military salary.
/// </summary>
/// <remarks>
/// TOR §3.14-B. Bază normativă: Legea 162/2005 privind statutul militarilor.
/// Engine note: the percent-of-fact amount kind requires
/// <c>lastMilitarySalaryMdl</c> to be supplied as a <c>Money</c> value.
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityInsuranceMilitaryPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityInsuranceMilitaryPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.14-B-DISABILITY-INSURANCE-MILITARY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Asigurare dizabilitate militar",
      "type": "object",
      "required": ["isMilitaryPersonnel", "disabilityFromServiceVerified", "lastMilitarySalaryMdl", "claimantIdnp"],
      "properties": {
        "isMilitaryPersonnel":           { "type": "boolean" },
        "disabilityFromServiceVerified": { "type": "boolean" },
        "lastMilitarySalaryMdl":         { "type": "number", "minimum": 0 },
        "claimantIdnp":                  { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// military personnel status and verified service-linked disability; the
    /// benefit is 100% of the last military salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_INS_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "DISABILITY_INS_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-equals", "fact": "disabilityFromServiceVerified", "value": true,
          "failCode": "DISABILITY_INS_MIL_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "DISABILITY_INS_MIL_ELIGIBLE"
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
            NameRo = "Asigurare dizabilitate militar",
            NameEn = "Military disability insurance",
            NameRu = "Страхование инвалидности военнослужащего",
            DescriptionRo =
                "Indemnizație de asigurare acordată militarilor cu dizabilitate cauzată de " +
                "serviciu, conform Legii 162/2005.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-INS-MIL-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
