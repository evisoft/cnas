using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.11-B — Pensie pentru boală profesională
/// (Occupational-disease pension) seed row. Eligibility requires that the
/// occupational disease has been verified and that the disability degree is
/// either <c>severe</c> or <c>accentuated</c>; the benefit is a tier-table
/// lookup keyed by disability degree.
/// </summary>
/// <remarks>
/// <para>TOR §3.11-B. Bază normativă: Legea 156/1998 privind sistemul public de
/// pensii. The tier table (3 500 / 2 500 MDL) is a Moldovan default — valoare
/// provizorie, de actualizat conform indexării anuale.</para>
/// <para>Engine note: the spec restricts eligibility to two degrees only, so
/// <c>fact-in-set</c> is used with the allowed degrees; the table similarly maps
/// only those two keys.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = OccupationalDiseasePensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class OccupationalDiseasePensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.11-B-OCCUPATIONAL-DISEASE-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie pentru boală profesională",
      "type": "object",
      "required": ["occupationalDiseaseVerified", "disabilityDegree", "claimantIdnp"],
      "properties": {
        "occupationalDiseaseVerified": { "type": "boolean" },
        "disabilityDegree":            { "type": "string", "enum": ["severe", "accentuated"] },
        "claimantIdnp":                { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// disease verification and that the disability degree is in the allowed set;
    /// the benefit is a tier-table lookup keyed by disability degree.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "OCC_DISEASE_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "occupationalDiseaseVerified", "value": true,
          "failCode": "OCC_DISEASE_PENSION_INELIGIBLE_NOT_VERIFIED" },
        { "rule": "fact-in-set", "fact": "disabilityDegree",
          "values": ["severe", "accentuated"],
          "failCode": "OCC_DISEASE_PENSION_INELIGIBLE_DEGREE" }
      ],
      "amount": {
        "kind": "table",
        "lookupFact": "disabilityDegree",
        "currency": "MDL",
        "table": {
          "severe":      3500.00,
          "accentuated": 2500.00
        }
      },
      "successCode": "OCC_DISEASE_PENSION_ELIGIBLE"
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
            NameRo = "Pensie pentru boală profesională",
            NameEn = "Occupational-disease pension",
            NameRu = "Пенсия по профессиональному заболеванию",
            DescriptionRo =
                "Pensie lunară acordată persoanelor cu boală profesională verificată, " +
                "diferențiată pe grade de severitate, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-OCC-DISEASE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
