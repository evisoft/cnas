using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Render-output format selector for <see cref="IDocumentGenerationService"/>.
/// </summary>
/// <remarks>
/// PDF remains the legacy default (QuestPDF-rendered, with a minimal-PDF fallback on
/// runtimes that lack the QuestPDF native dependencies). DOCX is the format mandated by
/// Annex 7 of the technical-requirements document and is rendered via
/// <c>DocumentFormat.OpenXml</c> — it produces a valid Office Open XML file that opens
/// in Word, LibreOffice, and Google Docs.
/// </remarks>
public enum DocumentRenderFormat
{
    /// <summary>Portable Document Format — the legacy default.</summary>
    Pdf,

    /// <summary>Office Open XML word-processing document (<c>.docx</c>).</summary>
    Docx,
}

/// <summary>
/// UC08 supporting service — auto-generates the two documents that accompany an
/// examined dossier: <em>Fișa de calcul</em> (calculation sheet) and <em>Decizia</em>
/// (decision document). Both are rendered server-side, uploaded to MinIO, and persisted
/// as <see cref="Cnas.Ps.Core.Domain.Document"/> rows attached to the dossier.
/// </summary>
/// <remarks>
/// <para>
/// The decision engine (<see cref="Cnas.Ps.Application.Decisions.IDecisionEngine"/>)
/// is the single source of truth for eligibility + benefit amount. This service merely
/// renders the engine outcome — it never re-computes the amount (CLAUDE.md RULE 6).
/// </para>
/// <para>
/// External identifiers are always Sqid-encoded (CLAUDE.md RULE 3). The methods accept a
/// Sqid-encoded dossier id and return the Sqid id of the newly-created Document row.
/// </para>
/// <para>
/// The single-argument overloads default to <see cref="DocumentRenderFormat.Pdf"/> and
/// remain in place for backward compatibility. New callers should use the
/// <see cref="DocumentRenderFormat"/>-aware overloads.
/// </para>
/// </remarks>
public interface IDocumentGenerationService
{
    /// <summary>
    /// Generates the <em>Fișa de calcul</em> (calculation sheet) PDF for the supplied dossier,
    /// uploads it to MinIO under the <c>cnas-documents</c> bucket, persists a
    /// <see cref="Cnas.Ps.Core.Domain.Document"/> row, and returns the Sqid id of that row.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the Sqid id of the generated Document on
    /// success; failure codes include <see cref="ErrorCodes.InvalidSqid"/>,
    /// <see cref="ErrorCodes.NotFound"/> (dossier missing/inactive), or whatever code is
    /// propagated by the underlying storage layer (<see cref="ErrorCodes.FileUnavailable"/>
    /// and friends).
    /// </returns>
    Task<Result<string>> GenerateCalculationSheetAsync(
        string dossierId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the <em>Decizia</em> (decision document) PDF for the supplied dossier,
    /// uploads it to MinIO under the <c>cnas-documents</c> bucket, persists a
    /// <see cref="Cnas.Ps.Core.Domain.Document"/> row, and returns the Sqid id of that row.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the Sqid id of the generated Document on
    /// success; same failure-code semantics as
    /// <see cref="GenerateCalculationSheetAsync(string, CancellationToken)"/>.
    /// </returns>
    Task<Result<string>> GenerateDecisionAsync(
        string dossierId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GenerateCalculationSheetAsync(string, CancellationToken)"/> but
    /// lets the caller choose <see cref="DocumentRenderFormat.Pdf"/> (legacy default) or
    /// <see cref="DocumentRenderFormat.Docx"/> (Annex 7 Word template format).
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="format">Render output format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<string>> GenerateCalculationSheetAsync(
        string dossierId,
        DocumentRenderFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GenerateDecisionAsync(string, CancellationToken)"/> but lets the
    /// caller choose <see cref="DocumentRenderFormat.Pdf"/> (legacy default) or
    /// <see cref="DocumentRenderFormat.Docx"/> (Annex 7 Word template format).
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="format">Render output format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<string>> GenerateDecisionAsync(
        string dossierId,
        DocumentRenderFormat format,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// UC17 phase 2B — Renders an operator-uploaded persistent DOCX template by
    /// resolving the row identified by <paramref name="templateCode"/>, fetching the
    /// binary from storage, and substituting every <c>{{key}}</c> placeholder with
    /// the matching value from <paramref name="data"/>. Bypasses the
    /// dossier-centric pipeline (no dossier load, no decision-engine re-evaluation,
    /// no <c>Document</c> row insertion, no audit emission) — this overload returns
    /// the rendered bytes directly so callers can hand them off to whatever sink
    /// they choose (HTTP response, follow-up signing pipeline, preview UI, …).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a new overload instead of extending the existing surface.</b> The
    /// dossier generators carry significant orchestration that does not apply to
    /// arbitrary uploaded templates (Sqid decode → dossier load → engine
    /// re-evaluation → upload → audit). Adding a polymorphic <c>data</c>
    /// parameter to those methods would couple two very different code paths;
    /// the new overload keeps the typed dossier path and the dictionary-typed
    /// uploaded path cleanly separated.
    /// </para>
    /// <para>
    /// <b>Dispatch contract.</b> The method consults
    /// <see cref="IUploadedTemplateRenderer.CanRenderAsync"/> first; when no
    /// persistent row matches, returns <see cref="ErrorCodes.NotFound"/>. There
    /// is intentionally no fallthrough to a DI-baked
    /// <c>IDocxTemplate</c> — uploaded codes and DI codes live in distinct
    /// namespaces in the operator's mental model.
    /// </para>
    /// </remarks>
    /// <param name="templateCode">Stable kebab-case template code (case-insensitive match).</param>
    /// <param name="data">
    /// Placeholder values keyed by placeholder name. <see langword="null"/> is
    /// treated as an empty dictionary (every placeholder in the template is left
    /// verbatim). Case-sensitive key lookup — <c>{{Name}}</c> matches the
    /// dictionary key <c>Name</c> exactly, not <c>name</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the rendered DOCX bytes on
    /// success. Failure codes:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.NotFound"/> — no persistent template row matches the code.</item>
    ///   <item><see cref="ErrorCodes.FileUnavailable"/> — the row exists but the
    ///         backing binary cannot be retrieved from storage.</item>
    /// </list>
    /// </returns>
    Task<Result<byte[]>> GenerateFromUploadedTemplateAsync(
        string templateCode,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken cancellationToken = default);
}
