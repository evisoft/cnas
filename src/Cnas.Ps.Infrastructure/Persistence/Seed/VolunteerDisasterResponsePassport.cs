using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// Factory for the Annex 3.9-D — Indemnizație pentru voluntari de intervenție în
/// situații de urgență (Volunteer disaster-response indemnity) seed row.
/// Eligibility requires that the claimant served as a volunteer in a disaster
/// response and that the resulting disability is attested.
/// </summary>
/// <remarks>
/// <para>TOR §3.9-D. The 2 500 MDL fixed amount is a reasonable Moldovan default
/// — valoare provizorie, de actualizat conform Hotărârii de Guvern privind
/// indexarea anuală a prestațiilor sociale.</para>
/// </remarks>
/// <example>
/// <code>
/// var passport = VolunteerDisasterResponsePassport.Create(timeProvider);
/// dbContext.ServicePassports.Add(passport);
/// </code>
/// </example>
public static class VolunteerDisasterResponsePassport
{
    /// <summary>Stable passport code used by external systems and links.</summary>
    public const string Code = "SP-3.9-D-VOLUNTEER-DISASTER-RESPONSE";

    /// <summary>
    /// JSON-Schema fragment describing the citizen-facing intake form. Mirrors the
    /// minimum set of facts required by <see cref="DecisionRules"/>.
    /// </summary>
    public const string FormSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "title": "Indemnizație voluntar de intervenție",
      "type": "object",
      "required": ["wasDisasterVolunteer", "disabilityFromVolunteering", "claimantIdnp"],
      "properties": {
        "wasDisasterVolunteer":      { "type": "boolean" },
        "disabilityFromVolunteering":{ "type": "boolean" },
        "claimantIdnp":              { "type": "string", "pattern": "^[0-9]{13}$" }
      }
    }
    """;

    /// <summary>
    /// Declarative rule-set consumed by <c>IDecisionEngine</c>. Eligibility checks
    /// volunteer status and attested disability; the benefit is a fixed 2 500 MDL.
    /// </summary>
    public const string DecisionRules = """
    {
      "code": "VOLUNTEER_DISASTER",
      "eligibility": [
        { "rule": "fact-equals", "fact": "wasDisasterVolunteer", "value": true,
          "failCode": "VOLUNTEER_DISASTER_INELIGIBLE_NOT_VOLUNTEER" },
        { "rule": "fact-equals", "fact": "disabilityFromVolunteering", "value": true,
          "failCode": "VOLUNTEER_DISASTER_INELIGIBLE_NO_DISABILITY" }
      ],
      "amount": {
        "kind": "fixed",
        "value": 2500.00,
        "currency": "MDL"
      },
      "successCode": "VOLUNTEER_DISASTER_ELIGIBLE"
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
            NameRo = "Indemnizație pentru voluntari de intervenție",
            NameEn = "Volunteer disaster-response indemnity",
            NameRu = "Пособие добровольцам спасательных операций",
            DescriptionRo =
                "Indemnizație lunară acordată voluntarilor care au suferit dizabilitate " +
                "în urma participării la intervenții în situații de urgență.",
            FormSchemaJson = FormSchema,
            WorkflowCode = "WF-VOLUNTEER-DISASTER-001",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsProactive = false,
            DecisionRulesJson = DecisionRules,
            CreatedAtUtc = clock.UtcNow,
        };
    }
}
