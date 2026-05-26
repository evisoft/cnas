using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.5-G — Ajutor de deces pentru angajații din sectorul
/// public (Public-sector funeral allowance) seed row. Eligibility requires the
/// deceased to have been a public-sector employee and the claim to be filed
/// within one year of death; the benefit is a fixed 3 000 MDL.
/// </summary>
/// <remarks>
/// <para>TOR §3.5-G. Bază normativă: Legea 270/2018 privind sistemul unitar de
/// salarizare în sectorul bugetar și HG-urile aferente de aplicare.</para>
/// <para>The 3 000 MDL fixed value is a reasonable Moldovan default — valoare
/// provizorie, de tunat după publicarea HG.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = FuneralPublicSectorAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class FuneralPublicSectorAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.5-G-FUNERAL-PUBLIC-SECTOR";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Ajutor de deces pentru angajații din sectorul public",
      "type": "object",
      "required": [
        "deceasedWasPublicSectorEmployee", "dateOfDeathUtc", "claimDateUtc", "claimantIdnp"
      ],
      "properties": {
        "deceasedWasPublicSectorEmployee": { "type": "boolean" },
        "dateOfDeathUtc":                   { "type": "string", "format": "date-time" },
        "claimDateUtc":                     { "type": "string", "format": "date-time" },
        "claimantIdnp":                     { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// the public-sector employment status of the deceased and that the claim is
    /// filed within one year of death; the benefit is a fixed 3 000 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "FUNERAL_PUBLIC_SECTOR",
      "eligibility": [
        { "rule": "fact-equals", "fact": "deceasedWasPublicSectorEmployee", "value": true,
          "failCode": "FUNERAL_PUBLIC_SECTOR_INELIGIBLE_NOT_PUBLIC" },
        { "rule": "date-within-days", "fact": "dateOfDeathUtc",
          "referenceFact": "claimDateUtc", "maxDays": 365,
          "failCode": "FUNERAL_PUBLIC_SECTOR_INELIGIBLE_LATE_CLAIM" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 3000.00,
        "currency": "MDL"
      },
      "successCode": "FUNERAL_PUBLIC_SECTOR_ELIGIBLE"
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
            NameRo = "Ajutor de deces pentru angajații din sectorul public",
            NameEn = "Public-sector funeral allowance",
            NameRu = "Пособие на погребение работника бюджетной сферы",
            DescriptionRo =
                "Ajutor unic acordat la decesul angajaților din sectorul public bugetar, " +
                "în baza Legii 270/2018 și HG-urilor aferente.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-FUNERAL-PUBLIC-SECTOR-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
