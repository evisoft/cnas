using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Annex 2 — <c>Persoane asigurate</c> (insured-person) registry CRUD façade.
/// Owns the full lifecycle of an InsuredPerson entity: registration, lookup, search,
/// deceased-flag recording, and soft-deletion.
/// </summary>
/// <remarks>
/// All identifiers crossing this façade are external Sqid strings or canonical IDNP
/// strings per CLAUDE.md RULE 3. Business failures are returned as <see cref="Result"/>
/// values; only true exceptions (network, OOM) throw.
/// </remarks>
public interface IInsuredPersonService
{
    /// <summary>
    /// Registers a new insured person. Validates the IDNP via
    /// <see cref="Cnas.Ps.Core.ValueObjects.Idnp.TryCreate(string?)"/> and rejects
    /// duplicates (an active record with the same IDNP already exists).
    /// </summary>
    /// <param name="input">The registration payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Sqid-encoded id of the new insured-person row, or a failure with one of:
    /// <see cref="ErrorCodes.InvalidIdnp"/>, <see cref="ErrorCodes.Conflict"/>.
    /// </returns>
    Task<Result<string>> RegisterAsync(InsuredPersonRegistrationInput input, CancellationToken ct = default);

    /// <summary>
    /// Loads a single insured person by its Sqid id.
    /// </summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The insured-person projection, or a failure with
    /// <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<InsuredPersonOutput>> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Loads a single insured person by its IDNP. Validates the IDNP before querying.
    /// </summary>
    /// <param name="idnp">Candidate 13-digit IDNP.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The insured-person projection, or a failure with
    /// <see cref="ErrorCodes.InvalidIdnp"/> / <see cref="ErrorCodes.NotFound"/>.
    /// </returns>
    Task<Result<InsuredPersonOutput>> GetByIdnpAsync(string idnp, CancellationToken ct = default);

    /// <summary>
    /// Paged registry search by partial name OR IDNP substring (case-insensitive).
    /// </summary>
    /// <param name="nameOrIdnp">
    /// Optional substring filter applied to LastName, FirstName, Patronymic, or Idnp.
    /// When null/whitespace, returns all active insured persons.
    /// </param>
    /// <param name="page">Pagination request (clamped to [1, 200] page size).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Page of <see cref="InsuredPersonListItem"/> rows ordered by LastName, FirstName.</returns>
    Task<Result<PagedResult<InsuredPersonListItem>>> SearchAsync(
        string? nameOrIdnp,
        PageRequest page,
        CancellationToken ct = default);

    /// <summary>
    /// Flips <c>IsDeceased=true</c>, records <paramref name="dateOfDeath"/>, and emits a
    /// <c>INSURED_PERSON.DECEASED_RECORDED</c> audit event (Critical) so the change is
    /// mirrored to MLog.
    /// </summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="dateOfDeath">Date of death sourced from eCMND.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> MarkDeceasedAsync(string id, DateOnly dateOfDeath, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the insured person (<c>IsActive=false</c>). Never performs a hard
    /// delete — see CLAUDE.md cross-cutting "Soft Deletes".
    /// </summary>
    /// <param name="id">Sqid-encoded insured-person id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <see cref="ErrorCodes.InvalidSqid"/> / <see cref="ErrorCodes.NotFound"/>.</returns>
    Task<Result> DeactivateAsync(string id, CancellationToken ct = default);
}
