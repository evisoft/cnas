namespace Cnas.Ps.Application.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — frozen allow-list of polymorphic owner-type strings accepted
/// by <c>AttachmentRecord.OwnerEntityType</c>. The validator on
/// <c>AttachmentUploadDto</c> consults this list to reject unknown values BEFORE the
/// service is invoked; runtime callers that hand-roll the type string (e.g. an internal
/// import job) should reference these constants rather than spelling the literal at the
/// call site so a refactor can be done atomically.
/// </summary>
/// <remarks>
/// <para>
/// The string values intentionally match the CLR type's <c>nameof()</c> output so a
/// future test can validate every constant resolves to an existing type in
/// <c>Cnas.Ps.Core.Domain</c>. Adding a new owner type is a single-line addition here
/// plus the corresponding entry in <see cref="All"/>.
/// </para>
/// </remarks>
public static class AttachmentOwnerTypes
{
    /// <summary>Owning <c>ServiceApplication</c> (Cerere).</summary>
    public const string ServiceApplication = "ServiceApplication";

    /// <summary>Owning <c>WorkflowTask</c> (Sarcină).</summary>
    public const string WorkflowTask = "WorkflowTask";

    /// <summary>Owning <c>UserProfile</c>.</summary>
    public const string UserProfile = "UserProfile";

    /// <summary>Owning <c>Solicitant</c> (Applicant).</summary>
    public const string Solicitant = "Solicitant";

    /// <summary>Owning <c>Document</c> (signature scan, ancillary attachment).</summary>
    public const string Document = "Document";

    /// <summary>Owning <c>BenefitPayment</c> ledger row (e.g. bank-rejection scan).</summary>
    public const string BenefitPayment = "BenefitPayment";

    /// <summary>Owning <c>ProfileUpdateRequest</c> (R0362).</summary>
    public const string ProfileUpdateRequest = "ProfileUpdateRequest";

    /// <summary>Owning <c>ReportJob</c> (R0583) — carries the rendered export bytes.</summary>
    public const string ReportJob = "ReportJob";

    /// <summary>
    /// Owning <c>Declaration</c> (R0821 / BP 1.2-L) — carries the scanned PDF /
    /// image of the original paper declaration plus its OCR metadata. Set by
    /// <c>IDeclarationService.AttachScannedCopyAsync</c> on every successful
    /// upload.
    /// </summary>
    public const string Declaration = "Declaration";

    /// <summary>
    /// Owning <c>LaborBooklet</c> (R0920 / BP 2.3-A) — carries the scanned PDF /
    /// image of the citizen's paper Carnet de muncă booklet plus its OCR
    /// metadata. Set by <c>ILaborBookletService.AttachScannedCopyAsync</c> on
    /// every successful upload.
    /// </summary>
    public const string LaborBooklet = "LaborBooklet";

    /// <summary>The complete frozen allow-list — extend in lock-step with the constants above.</summary>
    public static readonly IReadOnlyCollection<string> All =
    [
        ServiceApplication,
        WorkflowTask,
        UserProfile,
        Solicitant,
        Document,
        BenefitPayment,
        ProfileUpdateRequest,
        ReportJob,
        Declaration,
        LaborBooklet,
    ];
}
