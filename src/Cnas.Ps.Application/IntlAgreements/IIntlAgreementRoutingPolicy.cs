using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.IntlAgreements;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — per-benefit-kind policy contract
/// for the international-agreements 3-level routing chain. Concrete
/// implementations declare:
/// <list type="bullet">
///   <item>the discriminator they cover (<see cref="BenefitKind"/>);</item>
///   <item>the human-readable label exposed in admin UIs;</item>
///   <item>the role codes that gate the three levels of reviewers;</item>
///   <item>the evidence-JSON schema they accept.</item>
/// </list>
/// The routing service consumes the policy at every transition — the same
/// generic <see cref="IntlAgreementReviewCase"/> aggregate works for every
/// benefit kind because routing rules are externalised here.
/// </summary>
/// <remarks>
/// Implementations are registered as <c>Scoped</c> in the
/// <c>IEnumerable&lt;IIntlAgreementRoutingPolicy&gt;</c> collection so the
/// service can resolve them by <see cref="BenefitKind"/> at request time.
/// </remarks>
public interface IIntlAgreementRoutingPolicy
{
    /// <summary>Discriminator selecting which benefit kind this policy applies to.</summary>
    IntlAgreementBenefitKind BenefitKind { get; }

    /// <summary>Human-readable label exposed in admin UIs (e.g. "Sick leave + maternity (intl)").</summary>
    string DisplayLabel { get; }

    /// <summary>Role code required to act as the level-1 (local-office) reviewer.</summary>
    string LocalReviewerRoleCode { get; }

    /// <summary>Role code required to act as the level-2 (regional-office) reviewer.</summary>
    string RegionalReviewerRoleCode { get; }

    /// <summary>Role code required to act as the level-3 (national / CNAS HQ) reviewer.</summary>
    string NationalReviewerRoleCode { get; }

    /// <summary>
    /// Validates the supplied evidence JSON against the policy-specific
    /// schema. Returns <see cref="Result.Success"/> when the payload is
    /// acceptable; otherwise a failure result with a stable error code +
    /// human-readable message. Implementations MUST NOT reach into the
    /// database or any external dependency — the call is synchronous and
    /// pure.
    /// </summary>
    /// <param name="evidenceJson">Raw evidence JSON; may be <c>null</c> or empty.</param>
    /// <returns>Success when accepted; failure carrying a stable error code otherwise.</returns>
    Result ValidateEvidence(string? evidenceJson);
}
