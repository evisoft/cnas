using System.Collections.Frozen;

namespace Cnas.Ps.Application.Prefill;

/// <summary>
/// R0552 / R0562 — frozen vocabulary of field names that the pre-fill API understands.
/// Used by:
/// <list type="bullet">
///   <item>The validator — rejects any caller-supplied field name not in <see cref="All"/>.</item>
///   <item>The per-source allow-list lookup — only fields named here can be returned
///     by a given source.</item>
///   <item>The merge logic — only fields named here are compared across sources.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Stability.</b> These names are part of the public API contract — adding a new
/// field is backward-compatible; renaming or removing one is a breaking change.
/// PascalCase by convention; the wire shape (the dictionary key on
/// <c>PrefillPayloadDto.Fields</c>) is identical.
/// </para>
/// <para>
/// <b>What lives here vs. what does not.</b> Only fields a real upstream registry
/// authoritatively governs. Fields the citizen owns themselves (e.g. application
/// note, preferred channel) are filled by the UI directly without an external
/// round-trip — those have no business appearing in this vocabulary.
/// </para>
/// </remarks>
public static class PrefillFields
{
    // ─── RSP-governed identity / demographic fields ─────────────────────

    /// <summary>Full legal name of the citizen (RSP).</summary>
    public const string FullName = "FullName";

    /// <summary>National identifier (IDNP) of the citizen (RSP).</summary>
    public const string NationalId = "NationalId";

    /// <summary>Date of birth (RSP).</summary>
    public const string DateOfBirth = "DateOfBirth";

    /// <summary>Gender of the citizen (RSP).</summary>
    public const string Gender = "Gender";

    /// <summary>Civil status (married / single / widowed / etc.) — RSP.</summary>
    public const string CivilStatus = "CivilStatus";

    // ─── Address fields (RSP authoritative; RSUD secondary) ─────────────

    /// <summary>Street + house line (RSP authoritative, RSUD secondary).</summary>
    public const string Address = "Address";

    /// <summary>City or locality (RSP authoritative, RSUD secondary).</summary>
    public const string City = "City";

    /// <summary>Region / raion (RSP authoritative, RSUD secondary).</summary>
    public const string Region = "Region";

    // ─── SI SFS-governed contact + financial fields ─────────────────────

    /// <summary>Citizen contact e-mail on file with SI SFS.</summary>
    public const string Email = "Email";

    /// <summary>Citizen contact phone on file with SI SFS.</summary>
    public const string Phone = "Phone";

    /// <summary>Beneficiary IBAN on file with SI SFS.</summary>
    public const string Iban = "Iban";

    /// <summary>Beneficiary bank name on file with SI SFS.</summary>
    public const string BankName = "BankName";

    /// <summary>
    /// Frozen set of every recognised field name. Backs the validator's allow-list
    /// check and the merge logic's "is this field name known?" branch.
    /// </summary>
    public static readonly FrozenSet<string> All = new[]
    {
        FullName, NationalId, DateOfBirth, Gender, CivilStatus,
        Address, City, Region,
        Email, Phone, Iban, BankName,
    }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// R0552 / R0562 — stable code names for the three upstream registries that can
/// contribute pre-fill data. Mirrors the constants on
/// <c>Cnas.Ps.Infrastructure.Services.ProfileRefreshService</c> but lives here so
/// the validator does not need an Infrastructure-layer dependency (CLAUDE.md
/// §1.1 — Application MUST NOT depend on Infrastructure).
/// </summary>
public static class PrefillSources
{
    /// <summary>State Population Register (authoritative civil registry).</summary>
    public const string Rsp = "RSP";

    /// <summary>State Register of Legal Persons (driver / vehicle data, secondary address source).</summary>
    public const string Rsud = "RSUD";

    /// <summary>State Tax Service (fiscal data + contact details + bank details).</summary>
    public const string SiSfs = "SI_SFS";

    /// <summary>Frozen allow-list — every caller-supplied source code must appear here.</summary>
    public static readonly FrozenSet<string> All = new[] { Rsp, Rsud, SiSfs }
        .ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// R0552 / R0562 — per-source allow-lists describing which fields each registry is
/// permitted to contribute. A request for a field outside the source's allow-list is
/// silently skipped (no value retrieved) and produces a Warning so the UI can hint
/// "we tried RSUD for FullName but RSUD does not carry that field".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an allow-list and not a free-for-all.</b> Each upstream registry has a
/// well-defined business domain — RSP owns civil identity, SI SFS owns fiscal /
/// contact data, RSUD owns address (secondary) and vehicle data. Letting any source
/// answer any question would invite the wrong registry to override the
/// authoritative one (e.g. a stale RSUD address overwriting a fresh RSP move). The
/// allow-list bakes the domain boundary into the code.
/// </para>
/// </remarks>
public static class PrefillSourceAllowList
{
    /// <summary>Fields RSP is authorised to contribute.</summary>
    public static readonly FrozenSet<string> Rsp = new[]
    {
        PrefillFields.FullName,
        PrefillFields.NationalId,
        PrefillFields.DateOfBirth,
        PrefillFields.Gender,
        PrefillFields.Address,
        PrefillFields.City,
        PrefillFields.Region,
        PrefillFields.CivilStatus,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Fields RSUD is authorised to contribute (only address-shape data).</summary>
    public static readonly FrozenSet<string> Rsud = new[]
    {
        PrefillFields.Address,
        PrefillFields.City,
        PrefillFields.Region,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Fields SI SFS is authorised to contribute (contact + bank details).</summary>
    public static readonly FrozenSet<string> SiSfs = new[]
    {
        PrefillFields.Email,
        PrefillFields.Phone,
        PrefillFields.Iban,
        PrefillFields.BankName,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns the allow-list for <paramref name="sourceCode"/>, or an empty set when
    /// the source code is unknown (defensive — the caller has already validated it).
    /// </summary>
    /// <param name="sourceCode">One of the <see cref="PrefillSources"/> constants.</param>
    public static FrozenSet<string> For(string sourceCode) => sourceCode switch
    {
        PrefillSources.Rsp => Rsp,
        PrefillSources.Rsud => Rsud,
        PrefillSources.SiSfs => SiSfs,
        _ => FrozenSet<string>.Empty,
    };
}

/// <summary>
/// R0552 / R0562 — priority table used to resolve same-field conflicts across
/// sources. Higher number wins. RSP is the authoritative civil registry; SI SFS
/// owns fiscal / contact data; RSUD is least authoritative for personal-identity
/// fields (it primarily carries address shadows derived from older registrations).
/// </summary>
/// <remarks>
/// <para>
/// When two queried sources both return a value for the same field, the winner is
/// the source with the highest priority; the discarded value is captured in a
/// Warning (<c>"FieldX: RSP=Y, RSUD=Z — RSP used"</c>) so the citizen / auditor can
/// see what was rejected.
/// </para>
/// </remarks>
public static class PrefillSourcePriority
{
    /// <summary>RSP wins ties — authoritative civil registry.</summary>
    public const int Rsp = 30;

    /// <summary>SI SFS wins over RSUD — fiscal data is fresher than RSUD's address shadow.</summary>
    public const int SiSfs = 20;

    /// <summary>RSUD loses ties — least authoritative for personal-identity fields.</summary>
    public const int Rsud = 10;

    /// <summary>
    /// Returns the priority for <paramref name="sourceCode"/>, or <c>0</c> for an
    /// unknown source (defensive — the caller has already validated the code).
    /// </summary>
    /// <param name="sourceCode">One of the <see cref="PrefillSources"/> constants.</param>
    public static int For(string sourceCode) => sourceCode switch
    {
        PrefillSources.Rsp => Rsp,
        PrefillSources.SiSfs => SiSfs,
        PrefillSources.Rsud => Rsud,
        _ => 0,
    };
}
