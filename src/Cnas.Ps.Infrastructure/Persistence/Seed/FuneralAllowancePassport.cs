using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-B — Ajutor de deces (Funeral allowance) seed row.
/// Eligibility requires that the deceased had insurance, that the payer is in a
/// recognized relationship to the deceased (spouse / child / parent), and that
/// the claim is submitted within one year of the date of death. The benefit is
/// a fixed amount in MDL.
/// </summary>
/// <remarks>
/// <para>
/// TOR §3.5-B. The Romanian rules permit an OR-combinator on insurance vs.
/// relationship (either the deceased was insured, or the spouse is the payer).
/// The current <c>JsonRulesDecisionEngine</c> evaluates eligibility as a flat AND
/// of independent rules and lacks an OR-combinator; this passport therefore
/// adopts the simpler conjunctive form spelled out in the task brief — require
/// <i>both</i> that the deceased was insured <i>and</i> that the relationship is
/// in the recognized set. A follow-up engine upgrade should add an OR-combinator
/// so the looser administrative rule can be expressed verbatim.
/// </para>
/// <para>
/// The 1 500 MDL fixed value is a reasonable Moldovan default; the actual figure
/// is set annually by Government Decision and can be tuned via passport upsert
/// without code changes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var passport = FuneralAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class FuneralAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-B-FUNERAL-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor de deces",
      "type": "object",
      "required": ["deceasedHadInsurance", "payerRelationshipToDeceased", "dateOfDeathUtc", "claimDateUtc"],
      "properties": {
        "deceasedHadInsurance":         { "type": "boolean" },
        "payerRelationshipToDeceased":  { "type": "string", "enum": ["spouse", "child", "parent"] },
        "dateOfDeathUtc":               { "type": "string", "format": "date-time" },
        "claimDateUtc":                 { "type": "string", "format": "date-time" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the deceased had insurance, that the payer is a recognized relative,
    /// and that the claim is submitted within one year of death; the benefit is a
    /// fixed 1 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "FUNERAL_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedHadInsurance", "value": true,
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-in-set", "fact": "payerRelationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_RELATIONSHIP" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "FUNERAL_ALLOWANCE_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1500.00,
        "currency": "MDL"
      },
      "successCode": "FUNERAL_ALLOWANCE_ELIGIBLE"
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
    /// var passport = FuneralAllowancePassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Ajutor de deces",
            NameEn = "Funeral allowance",
            NameRu = "Пособие на погребение",
            DescriptionRo =
                "Ajutor unic acordat persoanei care suportă cheltuielile de înmormântare " +
                "a persoanei asigurate decedate, conform Legii 289/2004.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-FUNERAL-ALLOWANCE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
