using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-F — Pensie de orfan (Orphan pension) seed row.
/// Eligibility requires the claimant to be the deceased's child, to have lost
/// both parents, and to be under 18 years of age; the benefit is 100% of the
/// deceased's average insured income.
/// </summary>
/// <remarks>
/// <para>TOR §3.5-F. Bază normativă: Legea 156/1998 privind sistemul public de
/// pensii — art. 50 (regimul agravat al pensiei de urmaș pentru orfanii de ambii
/// părinți).</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the
/// reference fact to be a <c>Money</c> value; <c>deceasedAverageInsuredIncomeMdl</c>
/// is therefore supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = OrphanPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class OrphanPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-F-ORPHAN-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de orfan",
      "type": "object",
      "required": [
        "relationshipToDeceased", "bothParentsDeceased", "survivorAgeYears",
        "deceasedAverageInsuredIncomeMdl", "claimantIdnp"
      ],
      "properties": {
        "relationshipToDeceased":           { "type": "string",  "enum": ["spouse", "child", "parent"] },
        "bothParentsDeceased":              { "type": "boolean" },
        "survivorAgeYears":                 { "type": "integer", "minimum": 0, "maximum": 30 },
        "deceasedAverageInsuredIncomeMdl":  { "type": "number",  "minimum": 0 },
        "claimantIdnp":                     { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the child relationship, the loss of both parents and the under-18 age cap;
    /// the benefit is 100% of the deceased's average insured income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "ORPHAN_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "child",
          "failCode": "ORPHAN_PENSION_INELIGIBLE_RELATIONSHIP" },
        { "rule": "fact-equals", "fact": "bothParentsDeceased", "value": true,
          "failCode": "ORPHAN_PENSION_INELIGIBLE_NOT_FULL_ORPHAN" },
        { "rule": "fact-less-than", "fact": "survivorAgeYears", "value": 18,
          "failCode": "ORPHAN_PENSION_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "ORPHAN_PENSION_ELIGIBLE"
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
            NameRo = "Pensie de orfan",
            NameEn = "Orphan pension",
            NameRu = "Пенсия сироте",
            DescriptionRo =
                "Pensie majorată acordată copiilor minori care au pierdut ambii părinți " +
                "asigurați, conform art. 50 din Legea 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-ORPHAN-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = true,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
