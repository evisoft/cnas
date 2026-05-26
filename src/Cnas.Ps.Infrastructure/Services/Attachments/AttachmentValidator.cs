using System.Text.RegularExpressions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — default <see cref="IAttachmentValidator"/> implementation.
/// Owns the magic-byte allow-list, the per-MIME extension cross-check, and the
/// filename sanitisation rules (slugify, lowercase, extension preservation). The
/// upper byte-cap is the service's responsibility (it owns the
/// <see cref="AttachmentOptions"/> reference); this validator focuses on shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allow-list source of truth.</b> The magic-byte signatures live inline in
/// <see cref="Signatures"/>. The MIME-to-extension map lives in
/// <see cref="ExtensionsByMime"/>. Adding a new accepted type requires editing both
/// maps in the same commit AND adding the MIME to
/// <see cref="AttachmentOptions.AllowedMimeTypes"/>.
/// </para>
/// <para>
/// <b>Slug rule.</b> The sanitised filename stem is lowercased and reduced to
/// <c>[a-z0-9-]</c>; any run of disallowed characters becomes a single dash. The
/// extension is preserved verbatim (lowercased). An empty stem (e.g. the user
/// uploaded a file named just <c>.pdf</c>) is filled with the literal
/// <c>file</c> so the row always has a non-empty stem.
/// </para>
/// </remarks>
public sealed class AttachmentValidator : IAttachmentValidator
{
    private readonly AttachmentOptions _options;

    /// <summary>Inline magic-byte signatures keyed by detected MIME type.</summary>
    private static readonly Dictionary<string, byte[][]> Signatures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = [[0x25, 0x50, 0x44, 0x46]], // %PDF
            ["image/jpeg"] =
            [
                [0xFF, 0xD8, 0xFF, 0xE0],
                [0xFF, 0xD8, 0xFF, 0xE1],
                [0xFF, 0xD8, 0xFF, 0xDB],
                [0xFF, 0xD8, 0xFF, 0xEE],
            ],
            ["image/png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]], // .PNG\r\n.\n
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] =
            [
                [0x50, 0x4B, 0x03, 0x04], // PK\003\004 (ZIP container header used by DOCX)
            ],
        };

    /// <summary>
    /// Acceptable file extensions per detected MIME type. Used for the defence-in-depth
    /// extension cross-check.
    /// </summary>
    private static readonly Dictionary<string, string[]> ExtensionsByMime =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = [".pdf"],
            ["image/jpeg"] = [".jpg", ".jpeg"],
            ["image/png"] = [".png"],
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = [".docx"],
        };

    /// <summary>Lowercased <c>[a-z0-9]</c> stem cleaner.</summary>
    private static readonly Regex StemSanitiser = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Builds the validator from configured options.</summary>
    /// <param name="options">Bound <see cref="AttachmentOptions"/>.</param>
    public AttachmentValidator(IOptions<AttachmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public Result<ValidatedAttachment> Validate(byte[] bytes, string declaredFileName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredFileName);

        if (bytes.Length == 0)
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.ValidationFailed, "Empty payload.");
        }

        if (bytes.LongLength > _options.MaxBytes)
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.FileTooLarge,
                $"Attachment exceeds the {_options.MaxBytes / (1024 * 1024)} MiB cap.");
        }

        // Magic-byte sniff — pick the first MIME whose signature prefix matches the
        // payload's leading bytes AND appears in the configured allow-list.
        var detected = TryDetectMime(bytes);
        if (detected is null)
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.FileTypeMismatch, "File signature does not match any allowed type.");
        }
        if (!_options.AllowedMimeTypes.Contains(detected, StringComparer.OrdinalIgnoreCase))
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.FileTypeMismatch,
                $"Detected MIME '{detected}' is not in the configured allow-list.");
        }

        // Path-traversal / nested-directory guard on the input string. The application
        // validator catches the common cases at the API boundary; we re-check here
        // because the validator is also reachable from non-API callers (background jobs).
        if (declaredFileName.Contains('/', StringComparison.Ordinal)
            || declaredFileName.Contains('\\', StringComparison.Ordinal)
            || declaredFileName.Contains("..", StringComparison.Ordinal))
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.ValidationFailed, "Filename must not contain path separators or '..' segments.");
        }

        // Extension cross-check — declared extension must match the detected MIME's
        // allow-list. Defence in depth against masquerade attacks.
        var extension = Path.GetExtension(declaredFileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)
            || !ExtensionsByMime[detected].Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Result<ValidatedAttachment>.Failure(
                ErrorCodes.FileTypeMismatch,
                $"Filename extension '{extension}' does not match detected MIME '{detected}'.");
        }

        // Slugify the stem.
        var rawStem = Path.GetFileNameWithoutExtension(declaredFileName).ToLowerInvariant();
        var cleanedStem = StemSanitiser.Replace(rawStem, "-").Trim('-');
        if (string.IsNullOrEmpty(cleanedStem))
        {
            cleanedStem = "file";
        }
        var safeFileName = cleanedStem + extension;

        return Result<ValidatedAttachment>.Success(new ValidatedAttachment(detected, safeFileName));
    }

    /// <summary>
    /// Returns the first MIME type whose magic-byte signature prefix matches the leading
    /// bytes of <paramref name="bytes"/>, or <see langword="null"/> on no match.
    /// </summary>
    /// <param name="bytes">Uploaded bytes.</param>
    /// <returns>Detected MIME or null.</returns>
    private static string? TryDetectMime(byte[] bytes)
    {
        foreach (var (mime, signatures) in Signatures)
        {
            foreach (var signature in signatures)
            {
                if (bytes.Length < signature.Length)
                {
                    continue;
                }
                var match = true;
                for (var i = 0; i < signature.Length; i++)
                {
                    if (bytes[i] != signature[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return mime;
                }
            }
        }
        return null;
    }
}
