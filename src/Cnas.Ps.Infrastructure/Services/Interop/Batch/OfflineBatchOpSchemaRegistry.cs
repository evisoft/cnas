using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — production implementation of
/// <see cref="IOfflineBatchOpSchemaRegistry"/>. Hosts one
/// <see cref="OfflineBatchOpSchema"/> per <see cref="AnnexFourBatchOp"/>
/// value covering the eleven Annex-4 ops shipped by R0634 and iter-72.
/// </summary>
/// <remarks>
/// <para>
/// <b>CSV vocabulary.</b> The request header for each op is a CSV row with
/// the exact column names listed below. The response header always starts
/// with <c>RowOrdinal,Status,ErrorCode</c> followed by the op-specific
/// columns. Empty / missing optional cells round-trip as the literal
/// empty string. Lists (e.g. paginated rows) are JSON-encoded into one
/// CSV cell.
/// </para>
/// <para>
/// <b>Header arrays are cached as <c>static readonly</c> fields</b> to keep
/// the CA1861 analyzer happy — the registry constructs each schema once
/// and reuses the cached arrays for the lifetime of the process.
/// </para>
/// </remarks>
public sealed class OfflineBatchOpSchemaRegistry : IOfflineBatchOpSchemaRegistry
{
    /// <summary>Page-size cap on the per-row contribution-history slice.</summary>
    public const int ContributionHistoryRowCap = 100;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    // ── Request headers ──────────────────────────────────────────────────
    private static readonly string[] HdrIdnp = { "Idnp" };
    private static readonly string[] HdrIdnpFromTo = { "Idnp", "FromMonth", "ToMonth" };
    private static readonly string[] HdrIdnpBenefitType = { "Idnp", "BenefitType" };
    private static readonly string[] HdrIdnpAgreementCode = { "Idnp", "AgreementCode" };
    private static readonly string[] HdrDecisionSqidPeriod = { "DecisionSqid", "Period" };
    private static readonly string[] HdrTaxpayerCode = { "TaxpayerCode" };
    private static readonly string[] HdrIdnoPeriod = { "Idno", "Period" };

    // ── Response headers (op-specific suffix after RowOrdinal,Status,ErrorCode) ──
    private static readonly string[] ResInsuredPersonStatus =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "IsRegistered", "AccountCode", "ActiveBenefitsCount", "AsOfUtc" };
    private static readonly string[] ResContributionHistory =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "RowsJson", "TotalContributionsInWindow", "MonthsInWindow", "Truncated" };
    private static readonly string[] ResBenefitsList =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "BenefitsJson" };
    private static readonly string[] ResPersonalAccountSnapshot =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "AccountCode", "LifetimeContributions", "LifetimeMonths", "AsOfUtc" };
    private static readonly string[] ResActiveDecisions =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "DecisionsJson", "AsOfUtc" };
    private static readonly string[] ResPaymentStatus =
        { "RowOrdinal", "Status", "ErrorCode", "DecisionSqid", "Period", "PaymentStatus", "AmountMdl", "PaidDate", "ChannelKind", "ReceiptReference" };
    private static readonly string[] ResPayerData =
        { "RowOrdinal", "Status", "ErrorCode", "TaxpayerCode", "PayerKind", "DisplayName", "RegistrationDate", "Status", "CountOfInsuredEmployees", "LastDeclarationMonth" };
    private static readonly string[] ResIsBenefitBeneficiary =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "BenefitType", "IsBeneficiary", "Reason", "EvaluationDate", "DecisionSqid" };
    private static readonly string[] ResContributionPaymentInfo =
        { "RowOrdinal", "Status", "ErrorCode", "Idno", "Period", "DeclarationStatus", "TotalDueMdl", "TotalPaidMdl", "Outstanding", "LatePenaltyMdl" };
    private static readonly string[] ResLegalApplicableForm =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "AgreementCode", "ApplicableForm", "FormSerialNumber", "IssueDate", "ValidUntil", "HostCountryCode" };
    private static readonly string[] ResWorkInsurancePeriod =
        { "RowOrdinal", "Status", "ErrorCode", "Idnp", "TotalMonths", "FirstInsuredMonth", "LastInsuredMonth", "CurrentlyInsured", "PeriodCount" };

    // ── Empty cell defaults for null DTO branches (one per op response width minus 3 prefix cols) ──
    private static readonly string[] EmptyCells5 = { "", "", "", "", "" };
    private static readonly string[] EmptyCells2 = { "", "" };
    private static readonly string[] EmptyCells3 = { "", "", "" };
    private static readonly string[] EmptyCells6 = { "", "", "", "", "", "" };
    private static readonly string[] EmptyCells7 = { "", "", "", "", "", "", "" };

    private readonly IReadOnlyDictionary<AnnexFourBatchOp, OfflineBatchOpSchema> _byOp;

    /// <summary>Builds the registry with every schema pre-wired.</summary>
    public OfflineBatchOpSchemaRegistry()
    {
        _byOp = new Dictionary<AnnexFourBatchOp, OfflineBatchOpSchema>
        {
            [AnnexFourBatchOp.GetInsuredPersonStatus] = BuildInsuredPersonStatus(),
            [AnnexFourBatchOp.GetContributionHistory] = BuildContributionHistory(),
            [AnnexFourBatchOp.GetBenefitsList] = BuildBenefitsList(),
            [AnnexFourBatchOp.GetPersonalAccountSnapshot] = BuildPersonalAccountSnapshot(),
            [AnnexFourBatchOp.GetActiveDecisions] = BuildActiveDecisions(),
            [AnnexFourBatchOp.GetPaymentStatus] = BuildPaymentStatus(),
            [AnnexFourBatchOp.GetPayerData] = BuildPayerData(),
            [AnnexFourBatchOp.IsBenefitBeneficiary] = BuildIsBenefitBeneficiary(),
            [AnnexFourBatchOp.GetContributionPaymentInfo] = BuildContributionPaymentInfo(),
            [AnnexFourBatchOp.GetLegalApplicableForm] = BuildLegalApplicableForm(),
            [AnnexFourBatchOp.GetWorkInsurancePeriod] = BuildWorkInsurancePeriod(),
        };
    }

    /// <inheritdoc />
    public OfflineBatchOpSchema Get(AnnexFourBatchOp opCode)
    {
        if (!_byOp.TryGetValue(opCode, out var schema))
        {
            throw new KeyNotFoundException($"No schema registered for op {opCode}.");
        }
        return schema;
    }

    /// <summary>Reads the <c>i</c>-th cell from the supplied list, returning empty when missing.</summary>
    /// <param name="cells">CSV cells of a single row.</param>
    /// <param name="i">Zero-based cell index.</param>
    /// <returns>The trimmed cell value, or empty.</returns>
    private static string Cell(IReadOnlyList<string> cells, int i)
        => i < cells.Count ? cells[i].Trim() : string.Empty;

    private static OfflineBatchOpSchema BuildInsuredPersonStatus() => new(
        OpCode: AnnexFourBatchOp.GetInsuredPersonStatus,
        RequestHeader: HdrIdnp,
        ResponseHeader: ResInsuredPersonStatus,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { Idnp = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells5; }
            var dto = JsonSerializer.Deserialize<InsuredPersonStatusDto>(json);
            if (dto is null) { return EmptyCells5; }
            return new[]
            {
                dto.IdnpHashPrefix,
                dto.IsRegistered.ToString(CultureInfo.InvariantCulture),
                dto.AccountCode ?? string.Empty,
                dto.ActiveBenefitsCount.ToString(CultureInfo.InvariantCulture),
                dto.AsOfUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        });

    private static OfflineBatchOpSchema BuildContributionHistory() => new(
        OpCode: AnnexFourBatchOp.GetContributionHistory,
        RequestHeader: HdrIdnpFromTo,
        ResponseHeader: ResContributionHistory,
        ParseRequestRow: cells => JsonSerializer.Serialize(new
        {
            Idnp = Cell(cells, 0),
            FromMonth = Cell(cells, 1),
            ToMonth = Cell(cells, 2),
        }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells5; }
            var dto = JsonSerializer.Deserialize<ContributionHistoryDto>(json);
            if (dto is null) { return EmptyCells5; }
            var truncated = dto.Months.Count > ContributionHistoryRowCap;
            var rows = truncated ? dto.Months.Take(ContributionHistoryRowCap).ToList() : dto.Months.ToList();
            return new[]
            {
                dto.IdnpHashPrefix,
                JsonSerializer.Serialize(rows, JsonOpts),
                dto.TotalContributionsInWindow.ToString(CultureInfo.InvariantCulture),
                dto.MonthsInWindow.ToString(CultureInfo.InvariantCulture),
                truncated.ToString(CultureInfo.InvariantCulture),
            };
        });

    private static OfflineBatchOpSchema BuildBenefitsList() => new(
        OpCode: AnnexFourBatchOp.GetBenefitsList,
        RequestHeader: HdrIdnp,
        ResponseHeader: ResBenefitsList,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { Idnp = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells2; }
            var dto = JsonSerializer.Deserialize<BenefitsListDto>(json);
            if (dto is null) { return EmptyCells2; }
            return new[] { dto.IdnpHashPrefix, JsonSerializer.Serialize(dto.Benefits, JsonOpts) };
        });

    private static OfflineBatchOpSchema BuildPersonalAccountSnapshot() => new(
        OpCode: AnnexFourBatchOp.GetPersonalAccountSnapshot,
        RequestHeader: HdrIdnp,
        ResponseHeader: ResPersonalAccountSnapshot,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { Idnp = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells5; }
            var dto = JsonSerializer.Deserialize<PersonalAccountSnapshotDto>(json);
            if (dto is null) { return EmptyCells5; }
            return new[]
            {
                dto.IdnpHashPrefix,
                dto.AccountCode,
                dto.LifetimeContributions.ToString(CultureInfo.InvariantCulture),
                dto.LifetimeMonths.ToString(CultureInfo.InvariantCulture),
                dto.AsOfUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        });

    private static OfflineBatchOpSchema BuildActiveDecisions() => new(
        OpCode: AnnexFourBatchOp.GetActiveDecisions,
        RequestHeader: HdrIdnp,
        ResponseHeader: ResActiveDecisions,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { Idnp = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells3; }
            var dto = JsonSerializer.Deserialize<ActiveDecisionsDto>(json);
            if (dto is null) { return EmptyCells3; }
            return new[]
            {
                dto.IdnpHashPrefix,
                JsonSerializer.Serialize(dto.Decisions, JsonOpts),
                dto.AsOfUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        });

    private static OfflineBatchOpSchema BuildPaymentStatus() => new(
        OpCode: AnnexFourBatchOp.GetPaymentStatus,
        RequestHeader: HdrDecisionSqidPeriod,
        ResponseHeader: ResPaymentStatus,
        ParseRequestRow: cells => JsonSerializer.Serialize(new
        {
            DecisionSqid = Cell(cells, 0),
            Period = Cell(cells, 1),
        }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells7; }
            var dto = JsonSerializer.Deserialize<PaymentStatusDto>(json);
            if (dto is null) { return EmptyCells7; }
            return new[]
            {
                dto.DecisionSqid,
                dto.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dto.PaymentStatus,
                dto.AmountMdl.ToString(CultureInfo.InvariantCulture),
                dto.PaidDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                dto.ChannelKind,
                dto.ReceiptReference ?? string.Empty,
            };
        });

    private static OfflineBatchOpSchema BuildPayerData() => new(
        OpCode: AnnexFourBatchOp.GetPayerData,
        RequestHeader: HdrTaxpayerCode,
        ResponseHeader: ResPayerData,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { TaxpayerCode = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells7; }
            var dto = JsonSerializer.Deserialize<PayerDataDto>(json);
            if (dto is null) { return EmptyCells7; }
            return new[]
            {
                dto.TaxpayerHashPrefix,
                dto.PayerKind,
                dto.DisplayName,
                dto.RegistrationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dto.Status,
                dto.CountOfInsuredEmployees.ToString(CultureInfo.InvariantCulture),
                dto.LastDeclarationMonth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            };
        });

    private static OfflineBatchOpSchema BuildIsBenefitBeneficiary() => new(
        OpCode: AnnexFourBatchOp.IsBenefitBeneficiary,
        RequestHeader: HdrIdnpBenefitType,
        ResponseHeader: ResIsBenefitBeneficiary,
        ParseRequestRow: cells => JsonSerializer.Serialize(new
        {
            Idnp = Cell(cells, 0),
            BenefitType = Cell(cells, 1),
        }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells6; }
            var dto = JsonSerializer.Deserialize<IsBenefitBeneficiaryDto>(json);
            if (dto is null) { return EmptyCells6; }
            return new[]
            {
                dto.IdnpHashPrefix,
                dto.BenefitType,
                dto.IsBeneficiary.ToString(CultureInfo.InvariantCulture),
                dto.Reason,
                dto.EvaluationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dto.DecisionSqid ?? string.Empty,
            };
        });

    private static OfflineBatchOpSchema BuildContributionPaymentInfo() => new(
        OpCode: AnnexFourBatchOp.GetContributionPaymentInfo,
        RequestHeader: HdrIdnoPeriod,
        ResponseHeader: ResContributionPaymentInfo,
        ParseRequestRow: cells => JsonSerializer.Serialize(new
        {
            Idno = Cell(cells, 0),
            Period = Cell(cells, 1),
        }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells7; }
            var dto = JsonSerializer.Deserialize<ContributionPaymentInfoDto>(json);
            if (dto is null) { return EmptyCells7; }
            return new[]
            {
                dto.IdnoHashPrefix,
                dto.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dto.DeclarationStatus,
                dto.TotalDueMdl.ToString(CultureInfo.InvariantCulture),
                dto.TotalPaidMdl.ToString(CultureInfo.InvariantCulture),
                dto.Outstanding.ToString(CultureInfo.InvariantCulture),
                dto.LatePenaltyMdl.ToString(CultureInfo.InvariantCulture),
            };
        });

    private static OfflineBatchOpSchema BuildLegalApplicableForm() => new(
        OpCode: AnnexFourBatchOp.GetLegalApplicableForm,
        RequestHeader: HdrIdnpAgreementCode,
        ResponseHeader: ResLegalApplicableForm,
        ParseRequestRow: cells => JsonSerializer.Serialize(new
        {
            Idnp = Cell(cells, 0),
            AgreementCode = Cell(cells, 1),
        }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells7; }
            var dto = JsonSerializer.Deserialize<LegalApplicableFormDto>(json);
            if (dto is null) { return EmptyCells7; }
            return new[]
            {
                dto.IdnpHashPrefix,
                dto.AgreementCode,
                dto.ApplicableForm,
                dto.FormSerialNumber ?? string.Empty,
                dto.IssueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                dto.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                dto.HostCountryCode,
            };
        });

    private static OfflineBatchOpSchema BuildWorkInsurancePeriod() => new(
        OpCode: AnnexFourBatchOp.GetWorkInsurancePeriod,
        RequestHeader: HdrIdnp,
        ResponseHeader: ResWorkInsurancePeriod,
        ParseRequestRow: cells => JsonSerializer.Serialize(new { Idnp = Cell(cells, 0) }, JsonOpts),
        SerializeResponseRow: json =>
        {
            if (json is null) { return EmptyCells6; }
            var dto = JsonSerializer.Deserialize<WorkInsurancePeriodDto>(json);
            if (dto is null) { return EmptyCells6; }
            return new[]
            {
                dto.IdnpHashPrefix,
                dto.TotalMonths.ToString(CultureInfo.InvariantCulture),
                dto.FirstInsuredMonth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                dto.LastInsuredMonth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                dto.CurrentlyInsured.ToString(CultureInfo.InvariantCulture),
                dto.PeriodCount.ToString(CultureInfo.InvariantCulture),
            };
        });
}
