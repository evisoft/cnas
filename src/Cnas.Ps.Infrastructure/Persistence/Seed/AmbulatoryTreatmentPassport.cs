using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.4-C — Compensarea tratamentului ambulatoriu (Ambulatory
/// treatment compensation) seed row. Eligibility requires active insurance and a
/// positive commission approval; the benefit is 70% of the documented treatment
/// cost.
/// </summary>
/// <remarks>
/// <para>TOR §3.4-C. Bază normativă: Legea 1585/1998 privind asigurarea
/// obligatorie de asistență medicală și HG-urile MSMPS de aprobare a tratamentelor
/// compensate.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact to be a <c>Money</c> value; <c>treatmentCostMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = AmbulatoryTreatmentPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class AmbulatoryTreatmentPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.4-C-AMBULATORY-TREATMENT";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Compensarea tratamentului ambulatoriu",
      "type": "object",
      "required": ["isInsured", "treatmentApprovedByCommission", "treatmentCostMdl", "claimantIdnp"],
      "properties": {
        "isInsured":                     { "type": "boolean" },
        "treatmentApprovedByCommission": { "type": "boolean" },
        "treatmentCostMdl":              { "type": "number",  "minimum": 0 },
        "claimantIdnp":                  { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// active insurance and a positive commission approval; the benefit is 70% of
    /// the documented treatment cost.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "AMBULATORY_TREATMENT",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "AMBULATORY_TREATMENT_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-equals", "fact": "treatmentApprovedByCommission", "value": true,
          "failCode": "AMBULATORY_TREATMENT_INELIGIBLE_NOT_APPROVED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 70,
        "referenceFact": "treatmentCostMdl"
      },
      "successCode": "AMBULATORY_TREATMENT_ELIGIBLE"
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
            NameRo = "Compensarea tratamentului ambulatoriu",
            NameEn = "Ambulatory treatment compensation",
            NameRu = "Компенсация амбулаторного лечения",
            DescriptionRo =
                "Compensare a 70% din costul tratamentului ambulatoriu aprobat de comisia " +
                "medicală, pentru persoanele asigurate, conform Legii 1585/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-AMBULATORY-TREATMENT-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
