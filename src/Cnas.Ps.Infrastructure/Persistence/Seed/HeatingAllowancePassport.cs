using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-F — Compensație pentru încălzire (Heating allowance)
/// seasonal seed row. Eligibility requires a household to be certified vulnerable
/// and the claim to be filed within 180 days of the heating-season start; the
/// benefit is a fixed 1 200 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-F. Bază normativă: HG anuală privind compensațiile la energie
/// pentru perioada rece a anului. Suma de 1 200 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// <para>Engine note: the <c>date-within-days</c> rule treats <c>fact</c> as the
/// subject date and <c>referenceFact</c> as the later anchor, requiring
/// <c>referenceFact - fact</c> to be in <c>[0, maxDays]</c>. To express "claim
/// filed within 180 days of season start", we therefore set <c>fact</c> to
/// <c>heatingSeasonStartUtc</c> and <c>referenceFact</c> to <c>claimDateUtc</c>.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = HeatingAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class HeatingAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-F-HEATING-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Compensație pentru încălzire",
      "type": "object",
      "required": ["householdCertifiedVulnerable", "claimDateUtc", "heatingSeasonStartUtc", "claimantIdnp"],
      "properties": {
        "householdCertifiedVulnerable": { "type": "boolean" },
        "claimDateUtc":                 { "type": "string",  "format": "date-time" },
        "heatingSeasonStartUtc":        { "type": "string",  "format": "date-time" },
        "claimantIdnp":                 { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// vulnerable-household certification and that the claim is filed within 180
    /// days of the heating-season start; the benefit is a flat 1 200 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "HEATING_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "householdCertifiedVulnerable", "value": true,
          "failCode": "HEATING_ALLOWANCE_INELIGIBLE_NOT_VULNERABLE" },
        { "rule": "date-within-days", "fact": "heatingSeasonStartUtc",
          "referenceFact": "claimDateUtc", "maxDays": 180,
          "failCode": "HEATING_ALLOWANCE_INELIGIBLE_OUTSIDE_WINDOW" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1200.00,
        "currency": "MDL"
      },
      "successCode": "HEATING_ALLOWANCE_ELIGIBLE"
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
            NameRo = "Compensație pentru încălzire",
            NameEn = "Heating allowance",
            NameRu = "Компенсация на отопление",
            DescriptionRo =
                "Compensație sezonieră acordată gospodăriilor vulnerabile pentru perioada " +
                "rece a anului, conform HG anuale de compensare a energiei.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-HEATING-ALLOWANCE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
