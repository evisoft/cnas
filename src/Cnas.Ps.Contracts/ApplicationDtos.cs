namespace Cnas.Ps.Contracts;

/// <summary>
/// Input DTO used by UC06 — Solicitant submits an application for a public service.
/// IDs are Sqid-encoded; the service layer decodes them and validates ownership.
/// </summary>
/// <param name="ServicePassportId">Sqid of the service being requested.</param>
/// <param name="FormPayloadJson">Form payload (shape governed by the service passport).</param>
/// <param name="AttachmentDocumentIds">Sqid ids of previously uploaded documents.</param>
/// <param name="OnBehalfOfPrincipalIdnp">
/// Optional. When supplied, the authenticated operator is acting as a delegate for the
/// principal citizen identified by this IDNP. The service layer verifies a valid MPower
/// delegation for the (principal, delegate, service) tuple before persisting the
/// application against the principal's Solicitant record (UC06 CF 06.02, R0551).
/// </param>
public sealed record SubmitApplicationInput(
    string ServicePassportId,
    string FormPayloadJson,
    IReadOnlyList<string> AttachmentDocumentIds,
    string? OnBehalfOfPrincipalIdnp = null);

/// <summary>Output DTO surfacing the state of a submitted application.</summary>
public sealed record ApplicationOutput(
    string Id,
    string Status,
    string? ReferenceNumber,
    DateTime? SubmittedAtUtc);

/// <summary>Output projection used by UC03/UC12 listings.</summary>
public sealed record ApplicationListItemOutput(
    string Id,
    string Status,
    string? ReferenceNumber,
    string SolicitantId,
    DateTime CreatedAtUtc);
