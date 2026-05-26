using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0552 / R0562 — Pre-fill from RSP / RSUD / SI SFS (TOR CF 06.03 + CF 07.03)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0552 / R0562 / TOR CF 06.03 + CF 07.03 — request body for the pre-fill API. Both
/// the citizen-facing self-service surface and the staff-facing admin surface accept
/// the same shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Defaults.</b> A null or empty <see cref="Sources"/> array means "query all three
/// upstream registries" (RSP + RSUD + SI_SFS) — the most common case for an
/// application form that wants every reachable bit of authoritative data. A null or
/// empty <see cref="Fields"/> array means "return every field the queried sources are
/// willing to provide" within their per-source allow-list.
/// </para>
/// <para>
/// <b>Why a request body and not query parameters.</b> The pre-fill call is POST
/// because the response carries PII and POST keeps the field-selection list out of
/// proxy/server access logs (per CLAUDE.md §5.6 — never log PII identifiers in URLs).
/// </para>
/// </remarks>
/// <param name="Sources">
/// Optional explicit allow-list of upstream source codes. Each entry must be one of
/// <c>"RSP"</c>, <c>"RSUD"</c>, <c>"SI_SFS"</c> (case-sensitive). Null or empty array
/// = default to all three.
/// </param>
/// <param name="Fields">
/// Optional explicit allow-list of field names to return. Each entry must be a member
/// of the frozen <c>PrefillFields.All</c> vocabulary. Null or empty array = return all
/// fields available from the queried sources.
/// </param>
public sealed record PrefillRequestDto(
    IReadOnlyList<string>? Sources,
    IReadOnlyList<string>? Fields);

/// <summary>
/// R0552 / R0562 — one field value returned by the pre-fill API. The wire shape
/// captures the value as an opaque string; consumers parse it by field type (the
/// vocabulary in <c>PrefillFields.All</c> is the contract between the producing
/// gateway and the consuming form).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sensitivity.</b> Every individual field carries citizen PII (names, addresses,
/// IBAN, contact info), so the entire DTO is marked Confidential at the type level —
/// the <c>SensitivityClassificationAttribute</c> floor lifts every property even if
/// the individual property carries no annotation. Aligns with R0228 (no Restricted
/// here because no individual property is single-handedly identity-revealing —
/// IDNP is Restricted on its own but combined with NationalId field naming the
/// signal is the same as the Solicitant card itself which is Confidential).
/// </para>
/// </remarks>
/// <param name="Value">String-serialised value as returned by the upstream gateway.</param>
/// <param name="Source">Source code that contributed the winning value (<c>"RSP"</c>, <c>"RSUD"</c>, <c>"SI_SFS"</c>).</param>
/// <param name="RetrievedAtUtc">UTC instant the upstream gateway responded.</param>
[SensitivityClassification(SensitivityLabel.Confidential, Reason = "PII pulled from upstream civil/fiscal registries.")]
public sealed record PrefillFieldDto(
    string Value,
    string Source,
    DateTime RetrievedAtUtc);

/// <summary>
/// R0552 / R0562 — full response payload from the pre-fill API. Carries a per-field
/// dictionary of values plus diagnostic warnings (conflict resolution, gateway
/// timeouts, allow-list misses) and the per-field source attribution map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Warnings list rather than a partial-failure result.</b> Pre-fill is
/// advisory by design — the citizen still has to confirm every value before it lands
/// on the application form. A failure to retrieve one source should not block the
/// other two from contributing what they have. Warnings let the UI surface "we
/// could not reach SI SFS for your bank details — please enter them manually"
/// without rejecting the whole response.
/// </para>
/// <para>
/// <b>Source-attribution map.</b> <see cref="SourceUsedPerField"/> tells the UI
/// which registry contributed each value so the rendering can show a small badge
/// next to each pre-filled field ("verified by RSP"). This is also the seam future
/// auditors use when reconstructing what the citizen saw at fill-time.
/// </para>
/// </remarks>
/// <param name="SolicitantSqid">Sqid-encoded id of the Solicitant whose data was pulled.</param>
/// <param name="Fields">Field name → value map; only fields the gateways returned appear.</param>
/// <param name="Warnings">Human-readable diagnostics (conflicts, timeouts, allow-list misses).</param>
/// <param name="GeneratedAtUtc">Server timestamp (UTC) at which the response was assembled.</param>
/// <param name="SourceUsedPerField">Field name → winning source code (debug / UI badge use).</param>
[SensitivityClassification(SensitivityLabel.Confidential, Reason = "Carries citizen PII pulled from upstream registries.")]
public sealed record PrefillPayloadDto(
    string SolicitantSqid,
    IReadOnlyDictionary<string, PrefillFieldDto> Fields,
    IReadOnlyList<string> Warnings,
    DateTime GeneratedAtUtc,
    IReadOnlyDictionary<string, string> SourceUsedPerField);
