namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0512 / TOR CF 02.01 — CNAS regional branch surfaced by the anonymous
/// online-appointment booking directory. Each row represents a physical CNAS
/// office that citizens can schedule an in-person appointment with via the
/// external scheduler.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable code.</b> The natural key is <see cref="Code"/> (UTF-8 short
/// string, e.g. <c>"CHISINAU-CENTRU"</c>) rather than the surrogate
/// <see cref="AuditableEntity.Id"/>. The code participates in the public
/// deep-link contract and MUST remain stable across migrations — operators
/// edit other columns but the code is effectively immutable.
/// </para>
/// <para>
/// <b>External-id marker.</b> Implements <see cref="IExternalId"/> per the
/// task contract for the public services batch (R0511 / R0512 / R0513), even
/// though the Branch DTO surfaces <see cref="Code"/> rather than a Sqid-encoded
/// <c>Id</c> at the boundary. The marker signals that this entity participates
/// in public-facing surfaces; the actual external key is the code, not the
/// surrogate bigint.
/// </para>
/// <para>
/// <b>Seed-on-migrate.</b> Five default branches (Chișinău Centru, Bălți,
/// Cahul, Comrat, Edineț) are seeded by the
/// <c>AddCnasBranchesAndPublicLookups</c> migration via idempotent
/// <c>ON CONFLICT (Code) DO NOTHING</c> so operator edits are never overwritten
/// on re-run.
/// </para>
/// </remarks>
public sealed class CnasBranch : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable, URL-safe short code (e.g. <c>"CHISINAU-CENTRU"</c>, <c>"BALTI"</c>).
    /// Unique across active rows; participates in the public deep-link
    /// contract for the online-appointment endpoint.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>Display name of the branch (Romanian-language label).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Romanian display name. When null the resolver
    /// falls back to <see cref="Name"/>; when populated it overrides <see cref="Name"/>
    /// for callers asking for the <c>"ro"</c> culture. Capped at 256 chars at the
    /// persistence layer.
    /// </summary>
    public string? NameRo { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional Russian display name. Capped at 256 chars at
    /// the persistence layer; surfaced by <c>ILocalizedNameResolver</c> when the
    /// requested culture is <c>"ru"</c>.
    /// </summary>
    public string? NameRu { get; set; }

    /// <summary>
    /// R0027 / TOR ARH 022 — optional English display name. Capped at 256 chars at
    /// the persistence layer; surfaced by <c>ILocalizedNameResolver</c> when the
    /// requested culture is <c>"en"</c>.
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>City the branch is located in (Romanian-language label).</summary>
    public required string City { get; set; }

    /// <summary>Optional street address; <c>null</c> when not curated.</summary>
    public string? Address { get; set; }

    /// <summary>
    /// Optional E.164 contact phone (e.g. <c>"+37322123456"</c>); <c>null</c>
    /// when not curated.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Optional per-branch override of the deep-link URL template. When
    /// <c>null</c> the directory's shared template is used. Reserved for the
    /// rare case where a branch points at a different scheduler than the
    /// system-wide default.
    /// </summary>
    public string? OnlineSchedulingUrlTemplate { get; set; }
}
