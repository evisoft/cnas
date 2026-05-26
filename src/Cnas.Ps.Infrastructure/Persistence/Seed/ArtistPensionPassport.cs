using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.15-A — Pensie pentru artist profesionist
/// (Professional-artist pension) seed row. Eligibility requires the claimant to
/// be a recognized professional artist with more than 19 years of artistic
/// career; the benefit is a fixed monthly amount.
/// </summary>
/// <remarks>
/// <para>TOR §3.15-A. Bază normativă: Legea 156/1998 art. 47 (privind pensiile
/// pentru anumite categorii). The 3 500 MDL fixed amount is a Moldovan default
/// — valoare provizorie, de actualizat la indexarea anuală.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = ArtistPensionPassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class ArtistPensionPassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.15-A-ARTIST-PENSION";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Pensie artist profesionist",
      "type": "object",
      "required": ["wasProfessionalArtist", "artisticCareerYears", "claimantIdnp"],
      "properties": {
        "wasProfessionalArtist": { "type": "boolean" },
        "artisticCareerYears":   { "type": "integer", "minimum": 0 },
        "claimantIdnp":          { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// professional-artist status and a career &gt; 19 years; the benefit is a
    /// fixed 3 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "ARTIST_PENSION",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasProfessionalArtist", "value": true,
          "failCode": "ARTIST_PENSION_INELIGIBLE_NOT_ARTIST" },
        { "rule": "fact-greater-than", "fact": "artisticCareerYears", "value": 19,
          "failCode": "ARTIST_PENSION_INELIGIBLE_CAREER_YEARS" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3500.00,
        "currency": "MDL"
      },
      "successCode": "ARTIST_PENSION_ELIGIBLE"
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
            NameRo = "Pensie artist profesionist",
            NameEn = "Professional-artist pension",
            NameRu = "Пенсия профессиональному артисту",
            DescriptionRo =
                "Pensie lunară acordată artiștilor profesioniști cu peste 20 ani de carieră, " +
                "conform Legii 156/1998 art. 47.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-ARTIST-PENSION-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
