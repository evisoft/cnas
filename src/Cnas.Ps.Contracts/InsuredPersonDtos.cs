using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// Input DTO for registering a new <c>Persoană asigurată</c> (insured person) in the
/// Annex 2 registry per TOR §2.1 / §2.3 #6.
/// </summary>
/// <remarks>
/// The natural-person primary identifier is the Moldovan IDNP — a 13-digit business
/// value object (CLAUDE.md RULE 4) validated at the application boundary via
/// <c>Cnas.Ps.Core.ValueObjects.Idnp</c> (Contracts has zero project references, so
/// the cref cannot be resolved at compile time). The IDNP is NOT a database key and
/// is therefore transported in plain form (not Sqid-encoded). All output identifiers
/// crossing the system boundary are Sqid-encoded per CLAUDE.md RULE 3.
/// </remarks>
/// <param name="Idnp">
/// 13-digit Moldovan IDNP of the insured person. Validated for length, allowed
/// century prefix (0/1/2), and mod-10 weighted checksum.
/// </param>
/// <param name="LastName">Family name as recorded in RSP. Max 100 characters.</param>
/// <param name="FirstName">Given name as recorded in RSP. Max 100 characters.</param>
/// <param name="Patronymic">Patronymic, when applicable. Max 100 characters.</param>
/// <param name="BirthDate">Date of birth as recorded in RSP. Must be in the past.</param>
/// <example>
/// <code>
/// new InsuredPersonRegistrationInput(
///     Idnp: "2000123456782",
///     LastName: "Popescu",
///     FirstName: "Ion",
///     Patronymic: "Vasilevici",
///     BirthDate: new DateOnly(1980, 5, 12));
/// </code>
/// </example>
public sealed record InsuredPersonRegistrationInput(
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string Idnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string LastName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FirstName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Patronymic,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BirthDate);

/// <summary>
/// Output DTO representing a single insured-person record as it leaves the system.
/// </summary>
/// <remarks>
/// <para><see cref="Id"/> is a Sqid-encoded string (CLAUDE.md RULE 3).</para>
/// <para><see cref="Idnp"/> is the canonical business identifier (not a database key)
/// and is returned in plain form so external systems can correlate.</para>
/// </remarks>
/// <param name="Id">Sqid-encoded internal id of the insured person.</param>
/// <param name="Idnp">13-digit Moldovan IDNP (business identifier).</param>
/// <param name="LastName">Family name as recorded in RSP at last sync.</param>
/// <param name="FirstName">Given name as recorded in RSP at last sync.</param>
/// <param name="Patronymic">Patronymic, when known.</param>
/// <param name="BirthDate">Date of birth as recorded in RSP.</param>
/// <param name="IsDeceased">True when the person is currently flagged deceased per eCMND.</param>
/// <param name="DateOfDeath">Date of death, when <see cref="IsDeceased"/> is true.</param>
/// <param name="RegisteredAtUtc">UTC instant the person was first registered with CNAS.</param>
/// <param name="LastRspSyncUtc">Last successful RSP sync timestamp, when available.</param>
public sealed record InsuredPersonOutput(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string Idnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string LastName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FirstName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? Patronymic,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly BirthDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    bool IsDeceased,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    DateOnly? DateOfDeath,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RegisteredAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? LastRspSyncUtc);

/// <summary>
/// Compact projection used for paged registry listings (TOR UI 014).
/// </summary>
/// <param name="Id">Sqid-encoded internal id of the insured person.</param>
/// <param name="Idnp">13-digit Moldovan IDNP (business identifier).</param>
/// <param name="FullName">Display name composed from <c>LastName + FirstName + Patronymic</c>.</param>
/// <param name="IsDeceased">True when the person is currently flagged deceased per eCMND.</param>
public sealed record InsuredPersonListItem(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string Idnp,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string FullName,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    bool IsDeceased);
