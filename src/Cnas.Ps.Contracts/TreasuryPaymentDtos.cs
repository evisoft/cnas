using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0911 / TOR BP 2.2-B — Treasury payment receipts + per-receipt distribution
// across the matching REV-5 rows.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0911 / BP 2.2-B — outbound DTO for a Treasury payment receipt. Carries
/// the natural-key reference, the payment context, and the distribution
/// status set by the reconciliation background job.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the underlying receipt row.</param>
/// <param name="TreasuryReferenceNumber">Reference number assigned by the Treasury.</param>
/// <param name="ReceiptDate">Date the Treasury processed the payment.</param>
/// <param name="PayerContributorSqid">Sqid-encoded id of the paying Contributor.</param>
/// <param name="ReportingMonth">Calendar month the payment is attributed to (day = 1).</param>
/// <param name="AmountReceived">Amount received (MDL).</param>
/// <param name="DistributionStatus">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.TreasuryPaymentDistributionStatus</c> value
/// (<c>Pending</c>, <c>Distributed</c>, <c>PartiallyDistributed</c>,
/// <c>Failed</c>).
/// </param>
/// <param name="DistributedAtUtc">UTC instant the distribution job completed for this receipt.</param>
/// <param name="DistributionFailureReason">Stable failure reason when status is Failed.</param>
/// <param name="UndistributedRemainderAmount">Amount that could not be distributed (MDL).</param>
public sealed record TreasuryPaymentReceiptDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TreasuryReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReceiptDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PayerContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReportingMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AmountReceived,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DistributionStatus,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? DistributedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? DistributionFailureReason,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal? UndistributedRemainderAmount);

/// <summary>
/// R0911 / BP 2.2-B — input envelope for the
/// <c>POST /api/treasury-payments/import</c> endpoint. Carries the natural
/// key (TreasuryReferenceNumber), the payer + reporting-month context, and
/// the amount received.
/// </summary>
/// <param name="TreasuryReferenceNumber">External reference assigned by the Treasury (1..64 chars).</param>
/// <param name="ReceiptDate">Date the Treasury processed the payment.</param>
/// <param name="PayerContributorSqid">Sqid-encoded id of the paying Contributor.</param>
/// <param name="ReportingMonth">Calendar month the payment is attributed to (day = 1).</param>
/// <param name="AmountReceived">Amount received (MDL, &gt; 0, ≤ 100_000_000).</param>
public sealed record TreasuryPaymentReceiptImportInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TreasuryReferenceNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReceiptDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PayerContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly ReportingMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AmountReceived);
