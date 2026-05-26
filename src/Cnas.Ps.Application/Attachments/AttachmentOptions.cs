namespace Cnas.Ps.Application.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — tunable knobs for the attachment subsystem. Bound from the
/// <c>Cnas:Attachments</c> configuration section so operators can adjust the size
/// cap, the on-disk root, and the allow-list MIME types without redeploying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default rationale.</b>
/// <list type="bullet">
///   <item>
///     <description><see cref="MaxBytes"/> = 25 MiB — matches the existing
///     <c>Document</c> upload cap so the two upload surfaces stay aligned.</description>
///   </item>
///   <item>
///     <description><see cref="RootPath"/> = <c>./attachments</c> — relative to the
///     hosting process' working directory so local dev runs need zero config. Ops
///     overrides this to a mounted volume in container deployments.</description>
///   </item>
///   <item>
///     <description><see cref="AllowedMimeTypes"/> covers the most common citizen
///     uploads (PDF + JPEG + PNG + DOCX). Adding to the list requires adding a magic-
///     byte signature in <c>AttachmentValidator</c> in the same commit.</description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class AttachmentOptions
{
    /// <summary>Configuration section name used by the host bindings.</summary>
    public const string SectionName = "Cnas:Attachments";

    /// <summary>
    /// Hard cap on the decoded payload size. Uploads larger than this fail with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.FileTooLarge"/> (mapped to HTTP
    /// 413 PayloadTooLarge by the controller). Default 25 MiB.
    /// </summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Root directory used by the local-disk blob adapter. Created lazily on first
    /// write. The adapter rejects any storage key that resolves outside this root
    /// (path-traversal guard). Default <c>./attachments</c>.
    /// </summary>
    public string RootPath { get; set; } = "./attachments";

    /// <summary>
    /// MIME types accepted by the magic-byte validator. The string values are
    /// canonical IANA media types; the validator's magic-byte table is keyed by
    /// these strings, so adding a value here without also adding a corresponding
    /// signature is a no-op.
    /// </summary>
    public IReadOnlyList<string> AllowedMimeTypes { get; set; } =
    [
        "application/pdf",
        "image/jpeg",
        "image/png",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ];
}
