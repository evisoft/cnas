using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-F — Alocație pentru utilizatori de scaun cu rotile
/// (Wheelchair-user allowance) seed row. Eligibility requires confirmed wheelchair
/// use and verification by the medical commission; the benefit is a fixed 2 200
/// MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-F. Bază normativă: Legea 60/2012 privind incluziunea socială a
/// persoanelor cu dizabilități. Suma de 2 200 MDL este o valoare provizorie —
/// de actualizat după publicare HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = WheelchairUserAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class WheelchairUserAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-F-WHEELCHAIR-USER-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru utilizatori de scaun cu rotile",
      "type": "object",
      "required": ["usesWheelchair", "medicalCommissionVerified", "claimantIdnp"],
      "properties": {
        "usesWheelchair":            { "type": "boolean" },
        "medicalCommissionVerified": { "type": "boolean" },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// wheelchair-use status and medical-commission verification; the benefit is a
    /// flat 2 200 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "WHEELCHAIR_USER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "usesWheelchair", "value": true,
          "failCode": "WHEELCHAIR_USER_INELIGIBLE_NOT_WHEELCHAIR_USER" },
        { "rule": "fact-equals", "fact": "medicalCommissionVerified", "value": true,
          "failCode": "WHEELCHAIR_USER_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2200.00,
        "currency": "MDL"
      },
      "successCode": "WHEELCHAIR_USER_ELIGIBLE"
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
            NameRo = "Alocație pentru utilizatori de scaun cu rotile",
            NameEn = "Wheelchair-user allowance",
            NameRu = "Пособие для пользователей инвалидных колясок",
            DescriptionRo =
                "Alocație lunară acordată persoanelor care utilizează scaun cu rotile, " +
                "verificată de comisia medicală, conform Legii 60/2012.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-WHEELCHAIR-USER-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
