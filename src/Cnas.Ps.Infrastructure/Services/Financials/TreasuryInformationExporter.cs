using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Financials;

/// <summary>
/// R0816 / TOR BP 1.2-G — concrete implementation of
/// <see cref="ITreasuryInformationExporter"/>. Aggregates BASS refunds and
/// outstanding claims into a single XML/CSV payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Refund slice.</b> The query selects every active <see cref="BassRefund"/>
/// row in <see cref="BassRefundStatus.Approved"/> or
/// <see cref="BassRefundStatus.IssuedToTreasury"/> state whose
/// <see cref="BassRefund.IssuedDate"/> is null — these are the refunds the
/// Treasury still needs to dispatch.
/// </para>
/// <para>
/// <b>Claim slice.</b> The query selects every active
/// <see cref="Cnas.Ps.Core.Domain.Claim"/> row in
/// <see cref="ClaimStatus.Open"/> or <see cref="ClaimStatus.PartiallyPaid"/>
/// whose <c>OpenedDate</c> is within the past 30 days — these are the
/// contributions the Treasury is expected to wire.
/// </para>
/// <para>
/// <b>Cancelled refunds and Settled / Cancelled / Disputed claims</b> are
/// excluded by construction (the WHERE clauses filter them out).
/// </para>
/// </remarks>
public sealed class TreasuryInformationExporter : ITreasuryInformationExporter
{
    /// <summary>Stable format tag for the XML output.</summary>
    public const string FormatXml = "XML";

    /// <summary>Stable format tag for the CSV output.</summary>
    public const string FormatCsv = "CSV";

    /// <summary>Stable validation message when <c>forDate</c> is in the future.</summary>
    public const string ForDateInFutureMessage = "FOR_DATE_IN_FUTURE";

    /// <summary>Stable validation message when <c>format</c> is not XML or CSV.</summary>
    public const string FormatNotSupportedMessage = "FORMAT_NOT_SUPPORTED";

    /// <summary>Sliding window (days) used to scope outstanding-claim rows.</summary>
    public const int OutstandingWindowDays = 30;

    /// <summary>Projection record for the refund-slice query.</summary>
    /// <param name="Id">Raw refund id.</param>
    /// <param name="ContributorId">Raw paying-contributor id.</param>
    /// <param name="RelatedMonth">Reporting month.</param>
    /// <param name="RefundAmount">Refund amount (MDL).</param>
    /// <param name="Status">Persisted lifecycle status.</param>
    /// <param name="AuthorisationDocumentReference">Optional supporting document reference.</param>
    private sealed record RefundRow(
        long Id,
        long ContributorId,
        DateOnly RelatedMonth,
        decimal RefundAmount,
        BassRefundStatus Status,
        string? AuthorisationDocumentReference);

    /// <summary>Projection record for the outstanding-claim slice query.</summary>
    /// <param name="Id">Raw claim id.</param>
    /// <param name="ClaimNumber">External claim number.</param>
    /// <param name="ContributorId">Raw payer id.</param>
    /// <param name="RelatedMonth">Reporting month the claim attaches to.</param>
    /// <param name="RemainingAmount">Outstanding amount (MDL).</param>
    /// <param name="Status">Persisted lifecycle status.</param>
    private sealed record ClaimRow(
        long Id,
        string ClaimNumber,
        long ContributorId,
        DateOnly RelatedMonth,
        decimal RemainingAmount,
        ClaimStatus Status);

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the exporter with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (read surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder for outbound identifier opacity.</param>
    public TreasuryInformationExporter(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        _db = db;
        _clock = clock;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryInformationExportDto>> GenerateAsync(
        DateOnly forDate,
        string format,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(format);

        var todayUtc = DateOnly.FromDateTime(_clock.UtcNow);
        if (forDate > todayUtc)
        {
            return Result<TreasuryInformationExportDto>.Failure(
                ErrorCodes.ValidationFailed, ForDateInFutureMessage);
        }

        var normalisedFormat = format.Trim().ToUpperInvariant();
        if (normalisedFormat is not (FormatXml or FormatCsv))
        {
            return Result<TreasuryInformationExportDto>.Failure(
                ErrorCodes.ValidationFailed, FormatNotSupportedMessage);
        }

        // ── 1. Pending refunds ───────────────────────────────────────────
        // Approved + IssuedToTreasury rows whose dispatch is incomplete (i.e.
        // IssuedDate is still null) constitute the refunds the Treasury must
        // still wire. Cancelled rows are excluded by the Status filter and
        // soft-deleted rows by the IsActive filter.
        var refunds = await _db.BassRefunds
            .Where(r => r.IsActive
                && (r.Status == BassRefundStatus.Approved
                    || r.Status == BassRefundStatus.IssuedToTreasury)
                && r.IssuedDate == null)
            .OrderBy(r => r.Id)
            .Select(r => new RefundRow(
                r.Id,
                r.ContributorId,
                r.RelatedMonth,
                r.RefundAmount,
                r.Status,
                r.AuthorisationDocumentReference))
            .ToListAsync(ct).ConfigureAwait(false);

        // ── 2. Outstanding claims within the rolling 30-day window ───────
        var windowStart = forDate.AddDays(-OutstandingWindowDays);
        var claims = await _db.Claims
            .Where(c => c.IsActive
                && (c.Status == ClaimStatus.Open
                    || c.Status == ClaimStatus.PartiallyPaid)
                && c.OpenedDate >= windowStart
                && c.OpenedDate <= forDate)
            .OrderBy(c => c.Id)
            .Select(c => new ClaimRow(
                c.Id,
                c.ClaimNumber,
                c.ContributorId,
                c.RelatedMonth,
                c.RemainingAmount,
                c.Status))
            .ToListAsync(ct).ConfigureAwait(false);

        var totalRefund = refunds.Sum(r => r.RefundAmount);
        var totalOutstanding = claims.Sum(c => c.RemainingAmount);

        var fileSuffix = normalisedFormat == FormatXml ? "xml" : "csv";
        var fileName = $"treasury-info-{forDate:yyyy-MM-dd}.{fileSuffix}";

        byte[] content = normalisedFormat == FormatXml
            ? BuildXml(forDate, refunds, claims)
            : BuildCsv(refunds, claims);

        return Result<TreasuryInformationExportDto>.Success(new TreasuryInformationExportDto(
            Format: normalisedFormat,
            FileName: fileName,
            Content: content,
            RefundCount: refunds.Count,
            OutstandingClaimCount: claims.Count,
            TotalRefundAmount: totalRefund,
            TotalOutstandingAmount: totalOutstanding));
    }

    /// <summary>Encodes the aggregated payload as a UTF-8 XML document.</summary>
    /// <param name="forDate">Operating date stamped on the root element.</param>
    /// <param name="refunds">Refund slice projection.</param>
    /// <param name="claims">Outstanding-claim slice projection.</param>
    /// <returns>UTF-8-encoded XML bytes.</returns>
    private byte[] BuildXml(
        DateOnly forDate,
        IReadOnlyList<RefundRow> refunds,
        IReadOnlyList<ClaimRow> claims)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("TreasuryInformation",
                new XAttribute("forDate", forDate.ToString("O", CultureInfo.InvariantCulture)),
                new XElement("Refunds",
                    refunds.Select(r => new XElement("Refund",
                        new XAttribute("id", _sqids.Encode(r.Id)),
                        new XAttribute("contributorSqid", _sqids.Encode(r.ContributorId)),
                        new XAttribute("relatedMonth", r.RelatedMonth.ToString("O", CultureInfo.InvariantCulture)),
                        new XAttribute("amount", r.RefundAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XAttribute("status", r.Status.ToString()),
                        new XAttribute("authorisationDocumentReference", r.AuthorisationDocumentReference ?? string.Empty)))),
                new XElement("OutstandingClaims",
                    claims.Select(c => new XElement("Claim",
                        new XAttribute("id", _sqids.Encode(c.Id)),
                        new XAttribute("claimNumber", c.ClaimNumber),
                        new XAttribute("contributorSqid", _sqids.Encode(c.ContributorId)),
                        new XAttribute("relatedMonth", c.RelatedMonth.ToString("O", CultureInfo.InvariantCulture)),
                        new XAttribute("remainingAmount", c.RemainingAmount.ToString("F2", CultureInfo.InvariantCulture)),
                        new XAttribute("status", c.Status.ToString()))))));

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }

    /// <summary>Encodes the aggregated payload as a UTF-8 CSV document with section headers.</summary>
    /// <param name="refunds">Refund slice projection.</param>
    /// <param name="claims">Outstanding-claim slice projection.</param>
    /// <returns>UTF-8-encoded CSV bytes.</returns>
    private byte[] BuildCsv(
        IReadOnlyList<RefundRow> refunds,
        IReadOnlyList<ClaimRow> claims)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Refunds");
        sb.AppendLine("Id,ContributorSqid,RelatedMonth,Amount,Status,AuthorisationDocumentReference");
        foreach (var r in refunds)
        {
            sb.Append(_sqids.Encode(r.Id)).Append(',');
            sb.Append(_sqids.Encode(r.ContributorId)).Append(',');
            sb.Append(r.RelatedMonth.ToString("O", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.RefundAmount.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Status.ToString()).Append(',');
            // CSV escape: replace commas in the optional document reference with semicolons.
            var docRef = (r.AuthorisationDocumentReference ?? string.Empty).Replace(",", ";", StringComparison.Ordinal);
            sb.AppendLine(docRef);
        }
        sb.AppendLine();
        sb.AppendLine("# OutstandingClaims");
        sb.AppendLine("Id,ClaimNumber,ContributorSqid,RelatedMonth,RemainingAmount,Status");
        foreach (var c in claims)
        {
            sb.Append(_sqids.Encode(c.Id)).Append(',');
            sb.Append(c.ClaimNumber).Append(',');
            sb.Append(_sqids.Encode(c.ContributorId)).Append(',');
            sb.Append(c.RelatedMonth.ToString("O", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(c.RemainingAmount.ToString("F2", CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(c.Status.ToString());
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }
}
