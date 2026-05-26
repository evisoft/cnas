using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Profile output DTO (UC13 / R0621 / TOR CF 13.02). The profile aggregate
/// carries the caller's identity + contact slice and the recent issued-documents
/// summary that the citizen-self-service page renders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid invariant.</b> <see cref="Id"/> is the Sqid-encoded
/// <c>UserProfile.Id</c> per CLAUDE.md RULE 3 — never a raw long key.
/// </para>
/// <para>
/// <b>Aggregate scope (R0621 / TOR CF 13.02).</b> TOR CF 13.02 enumerates five
/// aggregate slices: identity, contact, representatives, applications, and
/// issued documents. Identity + contact ship as the flat fields on this DTO;
/// issued documents ship as <see cref="IssuedDocuments"/> below. Representatives
/// + applications are exposed by dedicated registry endpoints
/// (<c>/api/representatives</c>, <c>/api/applications/mine</c>) and are
/// intentionally NOT denormalised here — bundling them into the profile read
/// would inflate the response and duplicate the canonical registry shapes.
/// </para>
/// <para>
/// <b>Newest-first cap.</b> <see cref="IssuedDocuments"/> is capped at 50 rows
/// (service-side) ordered by issuance instant DESC. Older issuances are
/// reachable via the dedicated documents registry. The cap keeps the profile
/// payload bounded for the citizen self-service page.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded user-profile id (CLAUDE.md RULE 3).</param>
/// <param name="DisplayName">Citizen's display name (confidential).</param>
/// <param name="Email">Citizen's email address; <c>null</c> when not set.</param>
/// <param name="Phone">Citizen's E.164 phone; <c>null</c> when not set.</param>
/// <param name="PreferredLanguage">UI language preference (<c>ro</c>/<c>ru</c>/<c>en</c>).</param>
/// <param name="IssuedDocuments">
/// Newest-first summary (cap 50) of documents CNAS has issued inside dossiers
/// owned by the caller's Solicitant identity. Empty when no Solicitant is
/// linked to the user (the link is established by MConnect / RSP sync) or when
/// no issued documents exist yet. Never <c>null</c>.
/// </param>
public sealed record ProfileOutput(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Phone,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string PreferredLanguage,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<IssuedDocumentSummaryDto> IssuedDocuments);

/// <summary>Profile mutation DTO.</summary>
public sealed record ProfileUpdateInput(
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033.")]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Phone is citizen contact PII per R0228 / SEC 033.")]
    string? Phone,
    string PreferredLanguage);

/// <summary>
/// R0361 / UC13 — input shape accepted by <c>PUT /api/profile/contact</c>.
/// Carries the citizen's editable contact fields (display name, e-mail,
/// phone) for the self-service <c>MyProfile.razor</c> page. The language
/// preference is intentionally NOT exposed on this DTO — it has its own
/// thin endpoint at <c>PUT /api/profile/language</c> (R0211) because the
/// language toggle is high-frequency (every locale switch) while the
/// contact update is rare.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity fields are immutable.</b> Per CLAUDE.md §2.4 (mass-assignment
/// prevention) this input DTO excludes Id, IDNP, roles, IsActive, and
/// CreatedAtUtc — those are owned by the lifecycle endpoints, not the
/// self-service form.
/// </para>
/// <para>
/// <b>Email + Phone are nullable.</b> A <c>null</c> value clears the
/// previously-persisted field (PUT semantics, not PATCH). Phone is
/// normalised to canonical E.164 form by the service layer via
/// <c>PhoneE164.TryCreate</c>; malformed phones surface as
/// <c>ErrorCodes.InvalidPhone</c> rather than being silently dropped.
/// </para>
/// </remarks>
/// <param name="DisplayName">
/// The human-readable label rendered everywhere the user is identified.
/// Required: cannot be null, empty, or whitespace-only.
/// </param>
/// <param name="Email">
/// Optional RFC e-mail address. When present, must validate as a well-formed
/// address; <c>null</c> clears the previously-persisted value.
/// </param>
/// <param name="Phone">
/// Optional E.164 phone (e.g. <c>+37369123456</c>). When present, must validate
/// against <c>PhoneE164.TryCreate</c>; <c>null</c> clears the previously-persisted
/// value. Common formatting characters — spaces, dashes, parentheses — are
/// normalised at the service boundary.
/// </param>
public sealed record ProfileContactInput(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Email is citizen contact PII per R0228 / SEC 033.")]
    string? Email,
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Phone is citizen contact PII per R0228 / SEC 033.")]
    string? Phone);

/// <summary>
/// User notification-channel preferences DTO — R0171 (CF 22.02 / CF 04.08). Carried by
/// the <c>GET/PUT /api/profile/notification-preferences</c> endpoints; both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Sqid id.</b> The recipient is implicitly the authenticated caller (the controller
/// derives the user id from <c>ICallerContext</c>), so the DTO carries no identifier — it's
/// the recipient's own preferences either way.
/// </para>
/// <para>
/// <b>Categories deferred to R0173.</b> The <see cref="Categories"/> dictionary is persisted
/// and accepted on writes for forward compatibility, but is NOT consulted at dispatch in
/// R0171. Per-workflow notification strategy is the scope of the follow-up batch.
/// </para>
/// </remarks>
/// <param name="Email">Opt-in flag for the email channel.</param>
/// <param name="Sms">Opt-in flag for the SMS channel.</param>
/// <param name="InApp">Opt-in flag for the in-app inbox channel.</param>
/// <param name="Categories">Per-category opt-in flags (reserved for R0173). Never null —
/// pass an empty dictionary when the caller has not set any category-level preferences.</param>
public sealed record NotificationPreferencesDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential,
        Reason = "Even as an opt-in flag, the property name 'Email' carries Confidential per the R0228 naming convention; a rename to EmailEnabled is tracked as a deferred follow-up.")]
    bool Email,
    bool Sms,
    bool InApp,
    IReadOnlyDictionary<string, bool> Categories);
