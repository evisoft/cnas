using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-D — Pensie de invaliditate parțială pentru militari
/// (Partial-disability pension for military personnel) seed row. Eligibility requires
/// confirmed military-personnel status and a verified service-related disability; the
/// benefit is 70% of the last military salary.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-D. Bază normativă: Legea 1544/1993 privind asigurarea cu pensii a
/// militarilor. Valoarea procentuală (70%) este provizorie — de actualizat după
/// publicare HG/Lege.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>lastMilitarySalaryMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = PartialDisabilityMilitaryPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class PartialDisabilityMilitaryPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-D-PARTIAL-DISABILITY-MILITARY";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de invaliditate parțială pentru militari",
      "type": "object",
      "required": ["isMilitaryPersonnel", "disabilityFromServiceVerified", "lastMilitarySalaryMdl", "claimantIdnp"],
      "properties": {
        "isMilitaryPersonnel":           { "type": "boolean" },
        "disabilityFromServiceVerified": { "type": "boolean" },
        "lastMilitarySalaryMdl":         { "type": "number",  "minimum": 0 },
        "claimantIdnp":                  { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// confirmed military-personnel status and that the disability originated from
    /// service; the benefit is 70% of the last military salary.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "PARTIAL_DISABILITY_MIL",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isMilitaryPersonnel", "value": true,
          "failCode": "PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_MILITARY" },
        { "rule": "fact-equals", "fact": "disabilityFromServiceVerified", "value": true,
          "failCode": "PARTIAL_DISABILITY_MIL_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "lastMilitarySalaryMdl"
      },
      "successCode": "PARTIAL_DISABILITY_MIL_ELIGIBLE"
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
            NameRo = "Pensie de invaliditate parțială pentru militari",
            NameEn = "Partial-disability pension (military)",
            NameRu = "Пенсия по частичной инвалидности для военнослужащих",
            DescriptionRo =
                "Pensie de invaliditate parțială acordată militarilor cu dizabilitate verificată " +
                "ca fiind dobândită în serviciu, calculată ca 70% din ultima soldă militară.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-PARTIAL-DISABILITY-MIL-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
