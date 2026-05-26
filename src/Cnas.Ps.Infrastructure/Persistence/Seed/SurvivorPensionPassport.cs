using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-A — Pensie de urmaș (Survivor pension) seed row.
/// Eligibility requires that the deceased had insurance contributions and that
/// the claimant is in a qualifying relationship (spouse, child or parent).
/// The benefit is 75% of the deceased's average insured income.
/// </summary>
/// <remarks>
/// <para>
/// <b>Engine limitation:</b> the current <c>JsonRulesDecisionEngine</c> evaluates
/// eligibility rules as a flat AND of independent predicates — it has no
/// AND-of-OR combinator. The Romanian survivor-pension rules add an age cap
/// (≤ 18 years, or ≤ 23 if in full-time education) that applies <i>only</i> when
/// the relationship is <c>child</c>. Because we cannot express
/// "(relationship == child) IMPLIES (age ≤ 18)" with the current six rule kinds,
/// the caller is expected to request this passport only when the relationship
/// and survivor age are coherent (a guard implemented in the application layer
/// before invoking the engine). A follow-up engine upgrade should add a
/// conditional rule kind to internalize this constraint.
/// </para>
/// <para>
/// The 75% rate is a reasonable Moldovan default for the primary survivor and
/// can be tuned later via passport upsert.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var passport = SurvivorPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class SurvivorPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-A-SURVIVOR-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie de urmaș",
      "type": "object",
      "required": ["deceasedHadInsurance", "relationshipToDeceased", "survivorAgeYears", "deceasedAverageInsuredIncomeMdl"],
      "properties": {
        "deceasedHadInsurance":             { "type": "boolean" },
        "relationshipToDeceased":           { "type": "string", "enum": ["spouse", "child", "parent"] },
        "survivorAgeYears":                 { "type": "integer", "minimum": 0 },
        "deceasedAverageInsuredIncomeMdl":  { "type": "number",  "minimum": 0 }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the deceased had insurance and that the relationship to the deceased
    /// is a recognized one; the benefit is 75% of the deceased's average insured
    /// income.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "SURVIVOR_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedHadInsurance", "value": true,
          "failCode": "SURVIVOR_PENSION_INELIGIBLE_NOT_INSURED" },
        { "rule": "fact-in-set", "fact": "relationshipToDeceased",
          "values": ["spouse", "child", "parent"],
          "failCode": "SURVIVOR_PENSION_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 75,
        "referenceFact": "deceasedAverageInsuredIncomeMdl"
      },
      "successCode": "SURVIVOR_PENSION_ELIGIBLE"
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
    /// var passport = SurvivorPensionPassport.Create(timeProvider);
    /// </code>
    /// </example>
    public static ServicePassport Create(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new ServicePassport
        {
            Code = Code,
            NameRo = "Pensie de urmaș",
            NameEn = "Survivor pension",
            NameRu = "Пенсия по случаю потери кормильца",
            DescriptionRo =
                "Pensie acordată urmașilor (soț/soție, copil, părinte) ai persoanei " +
                "decedate care a realizat stagiul de cotizare, conform Legii 156/1998.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-SURVIVOR-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
