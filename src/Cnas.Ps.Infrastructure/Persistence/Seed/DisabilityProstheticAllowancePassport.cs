using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-F — Compensare a costului protezelor (Disability
/// prosthetic allowance) seed row. Eligibility requires a positive medical
/// prescription and active insurance; the benefit is 100% of the documented
/// prosthetic cost.
/// </summary>
/// <remarks>
/// <para>TOR §3.3-F. Bază normativă: Legea 60/2012 și HG 1413/2016 privind
/// compensarea costului protezelor și mijloacelor ajutătoare.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact to be a <c>Money</c> value; <c>prostheticCostMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityProstheticAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityProstheticAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-F-DISABILITY-PROSTHETIC";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Compensare a costului protezelor",
      "type": "object",
      "required": ["prostheticPrescribed", "isInsured", "prostheticCostMdl", "claimantIdnp"],
      "properties": {
        "prostheticPrescribed": { "type": "boolean" },
        "isInsured":            { "type": "boolean" },
        "prostheticCostMdl":    { "type": "number",  "minimum": 0 },
        "claimantIdnp":         { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that a prosthetic was prescribed and that the claimant has active insurance;
    /// the benefit is 100% of the documented prosthetic cost.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_PROSTHETIC",
      "eligibility": [
        { "rule": "fact-equals", "fact": "prostheticPrescribed", "value": true,
          "failCode": "DISABILITY_PROSTHETIC_INELIGIBLE_NO_PRESCRIPTION" },
        { "rule": "fact-equals", "fact": "isInsured", "value": true,
          "failCode": "DISABILITY_PROSTHETIC_INELIGIBLE_NOT_INSURED" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "prostheticCostMdl"
      },
      "successCode": "DISABILITY_PROSTHETIC_ELIGIBLE"
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
            NameRo = "Compensare a costului protezelor",
            NameEn = "Disability prosthetic allowance",
            NameRu = "Компенсация стоимости протезов",
            DescriptionRo =
                "Compensare integrală a costului protezelor și mijloacelor ajutătoare prescrise " +
                "persoanelor asigurate, conform HG 1413/2016.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-PROSTHETIC-001",
            MaxProcessingDays = 45,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
