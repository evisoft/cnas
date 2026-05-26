using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.6-G — Indemnizație pentru asistență parentală
/// profesionistă (Foster-care allowance) seed row. Eligibility requires a licensed
/// foster family with at least one child currently in care; the benefit is 100% of
/// the reference foster-care rate set annually by Government Decision.
/// </summary>
/// <remarks>
/// <para>TOR §3.6-G. Bază normativă: Legea 140/2013 privind protecția specială a
/// copiilor aflați în situație de risc și HG-urile anuale care indexează rata de
/// referință.</para>
/// <para>Engine note: the <c>percent-of-fact</c> amount kind requires the reference
/// fact to be a <c>Money</c> value; <c>referenceFosterRateMdl</c> is therefore
/// supplied as <c>Money.Mdl(...)</c> by the caller.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = FosterCareAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class FosterCareAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.6-G-FOSTER-CARE-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație pentru asistență parentală profesionistă",
      "type": "object",
      "required": ["licensedFosterFamily", "childrenInCareCount", "referenceFosterRateMdl", "claimantIdnp"],
      "properties": {
        "licensedFosterFamily":   { "type": "boolean" },
        "childrenInCareCount":    { "type": "integer", "minimum": 0 },
        "referenceFosterRateMdl": { "type": "number",  "minimum": 0 },
        "claimantIdnp":           { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// active foster-family licensing and at least one child in care; the benefit
    /// is 100% of the reference foster-care monthly rate.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "FOSTER_CARE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "licensedFosterFamily", "value": true,
          "failCode": "FOSTER_CARE_INELIGIBLE_NOT_LICENSED" },
        { "rule": "fact-greater-than", "fact": "childrenInCareCount", "value": 0,
          "failCode": "FOSTER_CARE_INELIGIBLE_NO_CHILDREN" }
      ],
      "amount": {
        "kind": "percent-of-fact",
        "percent": 100,
        "referenceFact": "referenceFosterRateMdl"
      },
      "successCode": "FOSTER_CARE_ELIGIBLE"
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
            NameRo = "Indemnizație pentru asistență parentală profesionistă",
            NameEn = "Foster-care allowance",
            NameRu = "Пособие профессиональной приёмной семье",
            DescriptionRo =
                "Indemnizație lunară acordată familiilor licențiate ca asistenți parentali " +
                "profesioniști pentru copiii aflați în plasament, conform Legii 140/2013.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-FOSTER-CARE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
