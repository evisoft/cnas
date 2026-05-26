using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.8-E — Alocație pentru surdomuți (Deaf-mute allowance)
/// seed row. Eligibility requires confirmed deaf-mute status and verification by
/// the medical commission; the benefit is a fixed 1 600 MDL transfer.
/// </summary>
/// <remarks>
/// <para>TOR §3.8-E. Bază normativă: Legea 499/1999 privind alocațiile sociale de
/// stat. Suma de 1 600 MDL este o valoare provizorie — de actualizat după publicare
/// HG/Lege.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = DeafMuteAllowancePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class DeafMuteAllowancePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.8-E-DEAF-MUTE-ALLOWANCE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Alocație pentru surdomuți",
      "type": "object",
      "required": ["isDeafMute", "medicalCommissionVerified", "claimantIdnp"],
      "properties": {
        "isDeafMute":                { "type": "boolean" },
        "medicalCommissionVerified": { "type": "boolean" },
        "claimantIdnp":              { "type": "string",  "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// deaf-mute status and medical-commission verification; the benefit is a flat
    /// 1 600 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "DEAF_MUTE_ALLOWANCE",
      "eligibility": [
        { "rule": "fact-equals", "fact": "isDeafMute", "value": true,
          "failCode": "DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_DEAFMUTE" },
        { "rule": "fact-equals", "fact": "medicalCommissionVerified", "value": true,
          "failCode": "DEAF_MUTE_ALLOWANCE_INELIGIBLE_NOT_VERIFIED" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 1600.00,
        "currency": "MDL"
      },
      "successCode": "DEAF_MUTE_ALLOWANCE_ELIGIBLE"
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
            NameRo = "Alocație pentru surdomuți",
            NameEn = "Deaf-mute allowance",
            NameRu = "Пособие для глухонемых",
            DescriptionRo =
                "Alocație lunară acordată persoanelor surdomute, verificată de comisia medicală, " +
                "conform Legii 499/1999.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-DEAF-MUTE-ALLOWANCE-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
