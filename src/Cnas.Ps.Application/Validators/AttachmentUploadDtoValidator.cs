using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0227 / TOR UI 014 — FluentValidation rules for <see cref="AttachmentUploadDto"/>.
/// Defends the attachment endpoint at the API boundary against malformed shapes BEFORE
/// the service is invoked: owner-type allow-list, filename shape (extension required,
/// no path separators, length cap), base64 well-formedness + size cap, enum parsing
/// for the category and (optional) sensitivity label.
/// </summary>
/// <remarks>
/// <para>
/// <b>Size cap.</b> The base64 cap is the WIRE-form cap (base64 inflates by ~4/3); the
/// decoded byte cap is enforced again by the service after decoding. Doing both keeps
/// a malicious caller from streaming gigabytes of base64 just to have us reject the
/// payload after decoding.
/// </para>
/// <para>
/// <b>Path-traversal guard.</b> The filename rule rejects forward AND back slashes
/// (Windows) plus a leading <c>..</c> segment. The service additionally re-sanitises
/// the filename via <c>AttachmentValidator</c> before persisting.
/// </para>
/// </remarks>
public sealed class AttachmentUploadDtoValidator : AbstractValidator<AttachmentUploadDto>
{
    /// <summary>Hard cap on the base64-encoded payload string length. 25 MiB ÷ 3 × 4 + slack.</summary>
    public const int MaxBase64Length = (25 * 1024 * 1024 / 3 * 4) + 1024;

    /// <summary>Hard cap on the optional description field.</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>Hard cap on the declared filename.</summary>
    public const int MaxDeclaredFileNameLength = 255;

    /// <summary>Builds the validator with the full rule set.</summary>
    public AttachmentUploadDtoValidator()
    {
        RuleFor(x => x.OwnerEntityType)
            .NotEmpty().WithMessage("OwnerEntityType is required.")
            .Must(BeKnownOwnerType!)
            .When(x => !string.IsNullOrEmpty(x.OwnerEntityType))
            .WithMessage(
                $"OwnerEntityType must be one of: {string.Join(", ", AttachmentOwnerTypes.All)}.");

        RuleFor(x => x.OwnerSqid)
            .NotEmpty().WithMessage("OwnerSqid is required.");

        RuleFor(x => x.ContentBase64)
            .NotEmpty().WithMessage("ContentBase64 is required.");

        RuleFor(x => x.ContentBase64)
            .Must(BeWithinBase64Cap)
            .WithMessage($"ContentBase64 exceeds the {MaxBase64Length} char cap.")
            .Must(BeWellFormedBase64!)
            .WithMessage("ContentBase64 is not a valid base64 string.")
            .When(x => !string.IsNullOrEmpty(x.ContentBase64));

        RuleFor(x => x.DeclaredFileName)
            .NotEmpty().WithMessage("DeclaredFileName is required.")
            .MaximumLength(MaxDeclaredFileNameLength)
            .WithMessage($"DeclaredFileName exceeds the {MaxDeclaredFileNameLength} char cap.");

        RuleFor(x => x.DeclaredFileName)
            .Must(NotContainPathSeparators!)
            .WithMessage("DeclaredFileName must not contain path separators or '..' segments.")
            .Must(HaveExtension!)
            .WithMessage("DeclaredFileName must include an extension (e.g. .pdf, .jpg).")
            .When(x => !string.IsNullOrEmpty(x.DeclaredFileName));

        RuleFor(x => x.Category)
            .NotEmpty().WithMessage("Category is required.")
            .Must(BeKnownCategory!)
            .When(x => !string.IsNullOrEmpty(x.Category))
            .WithMessage("Category must be one of Identity / Income / Medical / LegalDocument / Photo / Other.");

        RuleFor(x => x.SensitivityLabel)
            .Must(BeKnownSensitivityLabel!)
            .When(x => !string.IsNullOrEmpty(x.SensitivityLabel))
            .WithMessage("SensitivityLabel must be one of Public / Internal / Confidential / Restricted.");

        RuleFor(x => x.Description)
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Description exceeds the {MaxDescriptionLength} char cap.")
            .When(x => !string.IsNullOrEmpty(x.Description));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> appears in
    /// <see cref="AttachmentOwnerTypes.All"/>.
    /// </summary>
    /// <param name="candidate">Candidate owner-type string.</param>
    /// <returns><c>true</c> when the string is in the frozen allow-list.</returns>
    private static bool BeKnownOwnerType(string candidate)
        => AttachmentOwnerTypes.All.Contains(candidate, StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when the base64 payload length is within the wire cap.
    /// </summary>
    /// <param name="payload">Candidate base64 string.</param>
    /// <returns><c>true</c> within cap.</returns>
    private static bool BeWithinBase64Cap(string? payload)
        => payload is null || payload.Length <= MaxBase64Length;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="payload"/> decodes as base64. The
    /// decoded buffer is discarded immediately — only the well-formedness signal is
    /// consumed here; the service computes the real digest after decoding.
    /// </summary>
    /// <param name="payload">Candidate base64 string.</param>
    /// <returns><c>true</c> on successful round-trip.</returns>
    private static bool BeWellFormedBase64(string payload)
    {
        Span<byte> buffer = payload.Length > 4096 ? new byte[payload.Length] : stackalloc byte[payload.Length];
        return Convert.TryFromBase64String(payload, buffer, out _);
    }

    /// <summary>
    /// Returns <c>true</c> when the filename does NOT contain a path separator or a
    /// leading <c>..</c> segment.
    /// </summary>
    /// <param name="fileName">Candidate filename.</param>
    /// <returns><c>true</c> when safe.</returns>
    private static bool NotContainPathSeparators(string fileName)
        => !fileName.Contains('/', StringComparison.Ordinal)
        && !fileName.Contains('\\', StringComparison.Ordinal)
        && !fileName.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when the filename contains at least one <c>.</c> followed
    /// by ≥ 1 character (i.e. has an extension).
    /// </summary>
    /// <param name="fileName">Candidate filename.</param>
    /// <returns><c>true</c> when an extension is present.</returns>
    private static bool HaveExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 && dot < fileName.Length - 1;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> parses to an
    /// <see cref="AttachmentCategory"/> enum value (case-sensitive).
    /// </summary>
    /// <param name="candidate">Candidate category string.</param>
    /// <returns><c>true</c> when known.</returns>
    private static bool BeKnownCategory(string candidate)
        => Enum.TryParse<AttachmentCategory>(candidate, ignoreCase: false, out _);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> parses to a
    /// <see cref="SensitivityLabel"/> enum value (case-sensitive).
    /// </summary>
    /// <param name="candidate">Candidate sensitivity-label string.</param>
    /// <returns><c>true</c> when known.</returns>
    private static bool BeKnownSensitivityLabel(string candidate)
        => Enum.TryParse<SensitivityLabel>(candidate, ignoreCase: false, out _);
}
