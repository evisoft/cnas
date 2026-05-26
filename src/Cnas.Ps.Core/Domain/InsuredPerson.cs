namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Persoană asigurată — natural person registered in CNAS records as having social-insurance
/// rights and liabilities. TOR §2.1 / §2.3 #6.
/// </summary>
/// <remarks>
/// Identity is sourced from RSP via MConnect. Local data is the slice CNAS owns:
/// the Personal Account (Cont personal) state, history of contributions, prestation rights.
/// </remarks>
public sealed class InsuredPerson : AuditableEntity, IExternalId
{
    /// <summary>IDNP (UTF-8 STRING(13)) — primary external key.</summary>
    /// <remarks>
    /// <para>
    /// Encrypted at rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7 / TOR SEC 035).
    /// Because every encryption samples a fresh nonce, equality lookups against this column
    /// (<c>WHERE Idnp == X</c>) cease to work in production; use the
    /// <see cref="IdnpHash"/> shadow column instead — see that property's remarks for the
    /// synchronization contract.
    /// </para>
    /// </remarks>
    public required string Idnp { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 of the canonicalized <see cref="Idnp"/>. Backs the unique
    /// index, equality lookups, and — critically — the Annex 6f Solicitant→InsuredPerson
    /// join (Cases-by-Age-Group). Stored as base64 (44 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronization is the application layer's responsibility.</b> Every site that
    /// writes <see cref="Idnp"/> MUST also write this column via
    /// <c>Cnas.Ps.Application.Abstractions.IDeterministicHasher.ComputeHash</c> on
    /// the same value. See <c>Solicitant.NationalIdHash</c> remarks for the rationale.
    /// </para>
    /// <para>
    /// Default is <see cref="string.Empty"/> so test factories that do not exercise hash-driven
    /// lookups can construct <see cref="InsuredPerson"/> aggregates without setting it.
    /// Production paths (<c>InsuredPersonService</c>, RSP sync jobs) MUST populate it before
    /// <c>SaveChanges</c>.
    /// </para>
    /// </remarks>
    public string IdnpHash { get; set; } = string.Empty;

    /// <summary>Family name as recorded in RSP at the time of the last sync.</summary>
    public required string LastName { get; set; }

    /// <summary>Given name as recorded in RSP at the time of the last sync.</summary>
    public required string FirstName { get; set; }

    /// <summary>Patronymic, if applicable.</summary>
    public string? Patronymic { get; set; }

    /// <summary>Date of birth as recorded in RSP.</summary>
    public DateOnly BirthDate { get; set; }

    /// <summary>UTC date the person was first registered in CNAS records.</summary>
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>True if the person is currently deceased per eCMND.</summary>
    public bool IsDeceased { get; set; }

    /// <summary>Date of death, populated from eCMND.</summary>
    public DateOnly? DateOfDeath { get; set; }

    /// <summary>Last successful sync timestamp from RSP — used to govern refresh cadence.</summary>
    public DateTime? LastRspSyncUtc { get; set; }
}
