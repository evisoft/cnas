using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-E — Indemnizație pentru văduva persoanei cu merite
/// deosebite (Widow of meritorious person allowance) seed row. Eligibility
/// requires the deceased to have held meritorious status and the claimant to be
/// the surviving spouse; the benefit is a fixed 2 500 MDL.
/// </summary>
/// <remarks>
/// <para>TOR §3.5-E. Bază normativă: Legea 1544/2002 privind pensiile pentru
/// merite deosebite și HG-urile de aplicare aferente.</para>
/// <para>The 2 500 MDL fixed value is a reasonable Moldovan default; the actual
/// amount is indexed annually by Government Decision and can be tuned via passport
/// upsert without code changes.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WidowOfMeritoriousPersonPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WidowOfMeritoriousPersonPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-E-WIDOW-OF-MERITORIOUS-PERSON";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru văduva persoanei cu merite deosebite",
      "type": "object",
      "required": ["deceasedWasMeritorious", "relationshipToDeceased", "claimantIdnp"],
      "properties": {
        "deceasedWasMeritorious":  { "type": "boolean" },
        "relationshipToDeceased":  { "type": "string",  "enum": ["spouse", "child", "parent"] },
        "claimantIdnp":            { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the deceased's meritorious status and the surviving-spouse relationship;
    /// the benefit is a fixed 2 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WIDOW_MERITORIOUS",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedWasMeritorious", "value": true,
          "failCode": "WIDOW_MERITORIOUS_INELIGIBLE_NOT_MERITORIOUS" },
        { "rule": "fact-equals", "fact": "relationshipToDeceased", "value": "spouse",
          "failCode": "WIDOW_MERITORIOUS_INELIGIBLE_RELATIONSHIP" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "WIDOW_MERITORIOUS_ELIGIBLE"
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
            NameRo = "Indemnizație pentru văduva persoanei cu merite deosebite",
            NameEn = "Widow of meritorious person allowance",
            NameRu = "Пособие вдове лица с особыми заслугами",
            DescriptionRo =
                "Indemnizație lunară acordată soțului supraviețuitor al unei persoane care a " +
                "beneficiat de pensia pentru merite deosebite, conform Legii 1544/2002.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WIDOW-MERITORIOUS-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
