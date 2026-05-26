using System.Text.Json;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies;

/// <summary>
/// R1201 / TOR §3.4-B — routing policy for sick-leave + maternity
/// indemnities under bilateral social-security agreements. Exposes the
/// reviewer role codes that gate each of the three routing levels and a
/// minimal evidence-shape check.
/// </summary>
/// <remarks>
/// <b>Role codes are PLACEHOLDERS.</b> The <c>IMR_*</c> codes used here
/// will be replaced by the canonical role identifiers when the
/// SecurityAdmin role-catalogue lands. Audit + integration tests pin the
/// current codes so any future rename is caught at CI time.
/// </remarks>
public sealed class IncapacityMaternityIntlAgreementRoutingPolicy
    : IIntlAgreementRoutingPolicy
{
    /// <summary>PLACEHOLDER role code — sick-leave / maternity local-office reviewer.</summary>
    public const string LocalRole = "IMR_LOCAL_OFFICE_REVIEWER";

    /// <summary>PLACEHOLDER role code — sick-leave / maternity regional-office reviewer.</summary>
    public const string RegionalRole = "IMR_REGIONAL_OFFICE_REVIEWER";

    /// <summary>PLACEHOLDER role code — sick-leave / maternity national / CNAS-HQ reviewer.</summary>
    public const string NationalRole = "IMR_NATIONAL_INTL_REVIEWER";

    /// <inheritdoc />
    public IntlAgreementBenefitKind BenefitKind => IntlAgreementBenefitKind.IncapacityMaternity;

    /// <inheritdoc />
    public string DisplayLabel => "Sick leave + maternity (international agreements)";

    /// <inheritdoc />
    public string LocalReviewerRoleCode => LocalRole;

    /// <inheritdoc />
    public string RegionalReviewerRoleCode => RegionalRole;

    /// <inheritdoc />
    public string NationalReviewerRoleCode => NationalRole;

    /// <inheritdoc />
    public Result ValidateEvidence(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            // The evidence envelope is optional at create-time; reviewers
            // may attach it on the way through the chain.
            return Result.Success();
        }

        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            // PLACEHOLDER schema: the JSON must be an object. Future
            // iterations will validate explicit fields (medical-certificate
            // ref, employer-confirmation ref, ...).
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure(
                    ErrorCodes.ValidationFailed,
                    "INTL_AGREEMENT.EVIDENCE.INCAPACITY_MATERNITY.NOT_OBJECT");
            }
            return Result.Success();
        }
        catch (JsonException)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "INTL_AGREEMENT.EVIDENCE.INCAPACITY_MATERNITY.BAD_JSON");
        }
    }
}
