using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.2-G — Majorare la pensie pentru asigurați vârstnici
/// (Insured age increase) seed row. Eligibility requires the claimant to be at
/// least 70 years old as of the claim date; the benefit is 10% of the current
/// monthly pension.
/// </summary>
/// <remarks>
/// <para>TOR §3.2-G. Bază normativă: HG-urile anuale de indexare care prevăd
/// majorarea pensiei pentru asigurații care au depășit vârsta de 70 de ani.</para>
/// <para>The 10% rate is a reasonable Moldovan default — valoare provizorie, de
/// tunat după publicarea HG.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>currentPensionMdl</c> is therefore supplied
/// as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = InsuredAgeIncreasePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class InsuredAgeIncreasePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.2-G-INSURED-AGE-INCREASE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Majorare la pensie pentru asigurați vârstnici",
      "type": "object",
      "required": ["dobUtc", "claimDateUtc", "currentPensionMdl", "claimantIdnp"],
      "properties": {
        "dobUtc":            { "type": "string", "format": "date-time" },
        "claimDateUtc":      { "type": "string", "format": "date-time" },
        "currentPensionMdl": { "type": "number", "minimum": 0 },
        "claimantIdnp":      { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// that the claimant has reached 70 years of age as of the claim date; the
    /// benefit is 10% of the current pension.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "INSURED_AGE_INCREASE",
      "eligibility": [
        { "rule": "age-at-date-between", "dobFact": "dobUtc",
          "referenceFact": "claimDateUtc", "min": 70, "max": 120,
          "failCode": "INSURED_AGE_INCREASE_INELIGIBLE_AGE" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 10,
        "referenceFact": "currentPensionMdl"
      },
      "successCode": "INSURED_AGE_INCREASE_ELIGIBLE"
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
            NameRo = "Majorare la pensie pentru asigurați vârstnici",
            NameEn = "Insured age increase",
            NameRu = "Надбавка к пенсии для пожилых застрахованных",
            DescriptionRo =
                "Majorare lunară de 10% la pensie acordată asiguraților care au împlinit " +
                "70 de ani, conform HG anuale de indexare.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-INSURED-AGE-INCREASE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = true,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
