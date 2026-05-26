using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — security boundary that turns a raw byte payload + claimed
/// filename into a <see cref="ValidatedAttachment"/> ready for persistence, or a
/// stable failure code. Owns the magic-byte allow-list, the MIME ↔ extension cross
/// check, and the filename sanitisation rules (path-separator stripping, slugify,
/// extension preservation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Magic-byte allow-list.</b> Only files whose leading bytes match a configured
/// signature pass the check; the MIME type stored on the row is the DETECTED type
/// (not the value the client claimed) so a malicious caller cannot smuggle a
/// disallowed kind through by mislabelling the upload. CLAUDE.md §5.1 / TOR SEC 010.
/// </para>
/// <para>
/// <b>Extension cross-check.</b> The detected MIME type must match the file's
/// extension (e.g. <c>.pdf</c> for <c>application/pdf</c>). A mismatch fails the
/// validation regardless of whether the magic bytes are valid — defence in depth
/// against masquerade attacks.
/// </para>
/// </remarks>
public interface IAttachmentValidator
{
    /// <summary>
    /// Validates <paramref name="bytes"/> against the magic-byte allow-list,
    /// cross-checks the detected MIME against <paramref name="declaredFileName"/>'s
    /// extension, sanitises the filename, and returns a <see cref="ValidatedAttachment"/>
    /// ready for persistence.
    /// </summary>
    /// <param name="bytes">Raw uploaded bytes.</param>
    /// <param name="declaredFileName">Uploader-supplied filename (may carry path separators).</param>
    /// <returns>
    /// On success a populated <see cref="ValidatedAttachment"/>. Failure codes are
    /// taken from <see cref="ErrorCodes"/>: <see cref="ErrorCodes.FileTooLarge"/>,
    /// <see cref="ErrorCodes.FileTypeMismatch"/>, <see cref="ErrorCodes.ValidationFailed"/>.
    /// </returns>
    Result<ValidatedAttachment> Validate(byte[] bytes, string declaredFileName);
}

/// <summary>
/// R0227 / TOR UI 014 — pure-data outcome of an <see cref="IAttachmentValidator.Validate"/>
/// call. Carries the DETECTED MIME type (which becomes the row's
/// <c>ContentType</c>) and the SANITISED filename (which becomes the row's
/// <c>FileName</c>). The original bytes are NOT included — the caller owns them.
/// </summary>
/// <param name="DetectedContentType">
/// MIME type chosen by the magic-byte sniff (e.g. <c>application/pdf</c>).
/// </param>
/// <param name="SafeFileName">
/// Sanitised filename: lowercased, slugified (only <c>[a-z0-9-]</c> in the stem),
/// extension preserved as <c>.ext</c>. Never contains path separators or NUL bytes.
/// </param>
public sealed record ValidatedAttachment(string DetectedContentType, string SafeFileName);
