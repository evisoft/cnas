using System.Text.Json;
using Cnas.Ps.Application.IntlAgreements;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.IntlAgreements.Policies;

/// <summary>
/// R1402 / TOR §3.6-C — routing policy for unemployment indemnities under
/// bilateral social-security agreements. Exposes the reviewer role codes
/// that gate each of the three routing levels and a minimal
/// evidence-shape check.
/// </summary>
/// <remarks>
/// <b>Role codes are PLACEHOLDERS.</b> The <c>UI_*</c> codes used here
/// will be replaced by the canonical role identifiers when the
/// SecurityAdmin role-catalogue lands. Audit + integration tests pin the
/// current codes so any future rename is caught at CI time.
/// </remarks>
public sealed class UnemploymentIntlAgreementRoutingPolicy
    : IIntlAgreementRoutingPolicy
{
    /// <summary>PLACEHOLDER role code — unemployment local-office reviewer.</summary>
    public const string LocalRole = "UI_LOCAL_OFFICE_REVIEWER";

    /// <summary>PLACEHOLDER role code — unemployment regional-office reviewer.</summary>
    public const string RegionalRole = "UI_REGIONAL_OFFICE_REVIEWER";

    /// <summary>PLACEHOLDER role code — unemployment national / CNAS-HQ reviewer.</summary>
    public const string NationalRole = "UI_NATIONAL_INTL_REVIEWER";

    /// <inheritdoc />
    public IntlAgreementBenefitKind BenefitKind => IntlAgreementBenefitKind.Unemployment;

    /// <inheritdoc />
    public string DisplayLabel => "Unemployment indemnity (international agreements)";

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
            return Result.Success();
        }

        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            // PLACEHOLDER schema: must be an object. Future iterations will
            // validate employment-history ref, ANOFM determination ref, ...
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure(
                    ErrorCodes.ValidationFailed,
                    "INTL_AGREEMENT.EVIDENCE.UNEMPLOYMENT.NOT_OBJECT");
            }
            return Result.Success();
        }
        catch (JsonException)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                "INTL_AGREEMENT.EVIDENCE.UNEMPLOYMENT.BAD_JSON");
        }
    }
}
