using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.3-E — Mijloc de transport pentru persoanele cu
/// dizabilități (Disability mobility vehicle) seed row. Eligibility requires a
/// severe disability degree and a positive medical recommendation; the "benefit"
/// is an in-kind asset grant rather than a cash transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.3-E. Bază normativă: Legea 60/2012 privind incluziunea socială a
/// persoanelor cu dizabilități și HG-urile aferente de implementare.</para>
/// <para>Engine note: the engine's <c>amount</c> section is mandatory and can only
/// emit a monetary value. Because this service is an asset grant (cărucior cu rotile
/// / autovehicul adaptat), the amount is recorded as <c>fixed 0 MDL</c> as a
/// sentinel and the operational follow-up is handled by the downstream workflow.
/// A future engine extension could add an <c>asset</c> amount kind so the contract
/// can be expressed natively.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = DisabilityMobilityVehiclePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DisabilityMobilityVehiclePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.3-E-DISABILITY-MOBILITY-VEHICLE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Mijloc de transport pentru persoanele cu dizabilități",
      "type": "object",
      "required": ["disabilityDegree", "medicalRecommendation", "claimantIdnp"],
      "properties": {
        "disabilityDegree":      { "type": "string",  "enum": ["severe", "accentuated", "medium"] },
        "medicalRecommendation": { "type": "boolean" },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the disability degree is severe and that the medical commission has
    /// issued a positive recommendation; the "amount" is a sentinel 0 MDL marker
    /// because the entitlement is an asset, not cash.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DISABILITY_MOBILITY_VEHICLE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "disabilityDegree", "value": "severe",
          "failCode": "DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_DEGREE" },
        { "rule": "fact-equals", "fact": "medicalRecommendation", "value": true,
          "failCode": "DISABILITY_MOBILITY_VEHICLE_INELIGIBLE_NO_RECOMMENDATION" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 0.00,
        "currency": "MDL"
      },
      "successCode": "DISABILITY_MOBILITY_VEHICLE_ELIGIBLE"
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
            NameRo = "Mijloc de transport pentru persoanele cu dizabilități",
            NameEn = "Disability mobility vehicle",
            NameRu = "Транспортное средство для лиц с инвалидностью",
            DescriptionRo =
                "Acordarea unui mijloc de transport (cărucior cu rotile sau autovehicul adaptat) " +
                "persoanelor cu dizabilitate severă, în baza recomandării comisiei medicale.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DISABILITY-MOBILITY-VEHICLE-001",
            MaxProcessingDays = 60,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
