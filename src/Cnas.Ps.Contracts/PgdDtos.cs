namespace Cnas.Ps.Contracts;

/// <summary>
/// R0117 / CF 14.11 / TOR §2.5.5 — input DTO for a one-shot PGD (Portalul guvernamental
/// de date) dataset publish operation. PGD is the Moldovan government open-data portal;
/// CNAS pushes public-interest datasets (e.g. anonymised statistical aggregates) so
/// citizens and downstream systems can consume them without scraping CNAS-specific UIs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from MCabinet.</b> MCabinet is the per-citizen dashboard; PGD is the
/// public open-data surface. The two integrations share HTTP plumbing internally but
/// expose separate publisher contracts so per-target configuration, alerting, and rate
/// limiting can diverge.
/// </para>
/// <para>
/// <b>Sensitivity: Internal.</b> Input DTO; the values are admin-supplied at the API
/// boundary. The dataset code and title are eventually echoed back on the open-data
/// portal but the request payload itself is internal to CNAS.
/// </para>
/// </remarks>
/// <param name="DatasetCode">
/// Stable PGD dataset code (≤ 64 chars). Acts as the upstream natural key — re-publishing
/// the same code replaces the prior revision on the PGD side.
/// </param>
/// <param name="Title">Display title for the dataset on the PGD portal (≤ 200 chars).</param>
/// <param name="Description">Free-text description of the dataset (≤ 1000 chars).</param>
/// <param name="PayloadJson">
/// Opaque dataset payload — typically a JSON document, but the publisher does not
/// inspect it beyond the size cap. Capped at 1 MiB (1 048 576 chars) to bound memory.
/// </param>
/// <param name="ContentType">
/// MIME type of <paramref name="PayloadJson"/>: <c>application/json</c>, <c>text/csv</c>,
/// etc. Forwarded verbatim to PGD's <c>Content-Type</c> header.
/// </param>
public sealed record PgdDatasetPublishInputDto(
    string DatasetCode,
    string Title,
    string Description,
    string PayloadJson,
    string ContentType);

/// <summary>
/// R0117 / CF 14.11 — output DTO carrying the outcome of a
/// <see cref="PgdDatasetPublishInputDto"/> publish call. Carries the PGD-issued external
/// reference (on accept) or the failure reason (on reject / skip).
/// </summary>
/// <remarks>
/// <b>Sensitivity: Internal.</b> Status codes themselves are public enum values; the
/// surrounding object is admin-facing only.
/// </remarks>
/// <param name="Status">Outcome of the publish attempt.</param>
/// <param name="PgdReferenceId">
/// External reference id returned by PGD when <see cref="Status"/> is
/// <see cref="PgdPublishStatus.Accepted"/>. Null on <see cref="PgdPublishStatus.Rejected"/>
/// and <see cref="PgdPublishStatus.Skipped"/>. Not a CNAS surrogate id — no Sqid encoding
/// required.
/// </param>
/// <param name="FailureReason">
/// Stable English failure reason when <see cref="Status"/> is
/// <see cref="PgdPublishStatus.Rejected"/> or <see cref="PgdPublishStatus.Skipped"/>;
/// null on <see cref="PgdPublishStatus.Accepted"/>. Safe to log.
/// </param>
public sealed record PgdPublishOutcomeDto(
    PgdPublishStatus Status,
    string? PgdReferenceId,
    string? FailureReason);

/// <summary>
/// R0117 / CF 14.11 — outcome categories returned by an
/// <c>IPgdPublisher.PublishAsync</c> call. Categories are part of the public surface
/// (charted by operations dashboards). Public sensitivity.
/// </summary>
public enum PgdPublishStatus
{
    /// <summary>PGD accepted the dataset; a fresh reference id is in the outcome.</summary>
    Accepted = 0,

    /// <summary>PGD rejected the dataset (validation failure or non-2xx upstream).</summary>
    Rejected = 1,

    /// <summary>
    /// The publisher short-circuited without calling PGD (typically because the
    /// publisher is unconfigured in this environment). Caller treats this as advisory:
    /// the local transaction must not roll back because the open-data mirror failed.
    /// </summary>
    Skipped = 2,
}
