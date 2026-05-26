namespace Cnas.Ps.Core.Common;

/// <summary>
/// R0936 / TOR §10.1 — canonical vocabulary for the steps of the CNAS
/// decision-approval chain. Each value identifies one signing tier in the
/// generic state machine described in <c>R0939</c> (8-state CNAS lifecycle).
/// </summary>
/// <remarks>
/// <para>
/// <b>2-level vs 3-level passports.</b> A Service Passport (R0140) configures
/// the approval depth on a per-service basis:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>2-level chain</b> (default for most R0950..R0959 birth-event
///       services): <see cref="UserCnas"/> → <see cref="ChiefCnas"/>. The
///       <see cref="DirectorOfDirectorate"/> step is skipped.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>3-level chain</b> (international-agreement services R0955 /
///       R1101 / R1300 / R1402 and any passport that opts in):
///       <see cref="UserCnas"/> → <see cref="DirectorOfDirectorate"/> →
///       <see cref="ChiefCnas"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Status mapping.</b> Successful completion of each level lands the
/// parent <see cref="Cnas.Ps.Core.Domain.ServiceApplication"/> in:
/// </para>
/// <list type="bullet">
///   <item><see cref="UserCnas"/> → <c>ApplicationStatus.PendingApproval</c> (SemnatăUtilizator)</item>
///   <item><see cref="DirectorOfDirectorate"/> → <c>ApplicationStatus.SignedByDirector</c> (SemnatăȘefulDirecției)</item>
///   <item><see cref="ChiefCnas"/> → <c>ApplicationStatus.Approved</c> (AprobatăȘefulCNAS)</item>
/// </list>
/// <para>
/// <b>Numeric stability.</b> The integer values are part of the persistence
/// contract (the workflow-task acl + audit envelopes round-trip the int) —
/// renumbering is a breaking change. Append new levels at the end if the
/// signing chain ever grows.
/// </para>
/// </remarks>
public enum WorkflowApprovalLevel
{
    /// <summary>
    /// Examiner / processing user (the CNAS "Utilizator CNAS" role) — first
    /// signer on every passport. Maps to <c>ApplicationStatus.PendingApproval</c>.
    /// </summary>
    UserCnas = 0,

    /// <summary>
    /// Direction head (Șeful direcției). Present only on 3-level passports
    /// (international-agreement chains). Maps to
    /// <c>ApplicationStatus.SignedByDirector</c>.
    /// </summary>
    DirectorOfDirectorate = 1,

    /// <summary>
    /// CNAS chief (Șeful CNAS) — final signer on every passport. Approval at
    /// this level flips the application into
    /// <c>ApplicationStatus.Approved</c> and triggers the payment-order
    /// dispatch pipeline (R0938).
    /// </summary>
    ChiefCnas = 2,
}
