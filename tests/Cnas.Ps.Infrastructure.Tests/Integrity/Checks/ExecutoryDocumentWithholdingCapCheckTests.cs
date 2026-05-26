using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;

namespace Cnas.Ps.Infrastructure.Tests.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant tests for
/// <see cref="ExecutoryDocumentWithholdingCapCheck"/>. Verifies the
/// <c>TotalWithheldMdl &lt;= TotalOwedMdl</c> cap.
/// </summary>
public sealed class ExecutoryDocumentWithholdingCapCheckTests
{
    private static ExecutoryDocument NewRow(decimal? owed, decimal withheld, string sn = "EXE-2026-000001")
        => new()
        {
            DocumentSeriesNumber = sn,
            DebtorIdnp = "2000000000007",
            DebtorIdnpHash = "hash-debtor",
            Kind = ExecutoryDocumentKind.CourtOrder,
            Status = ExecutoryDocumentStatus.Active,
            IssuedBy = "Court A",
            IssuedDate = new DateOnly(2026, 1, 1),
            EffectiveFrom = new DateOnly(2026, 1, 5),
            WithholdingMode = ExecutoryDocumentWithholdingMode.FixedAmount,
            WithholdingAmountMdl = 100m,
            PriorityRank = 1,
            CreditorAccountIban = "MD24AG000000000000000000",
            CreditorAccountIbanHash = "hash-iban",
            CreditorName = "Creditor X",
            TotalOwedMdl = owed,
            TotalWithheldMdl = withheld,
            RegisteredByUserId = 1,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };

    [Fact]
    public async Task RunAsync_UnderCap_NoFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        db.ExecutoryDocuments.Add(NewRow(owed: 1000m, withheld: 800m));
        await db.SaveChangesAsync();

        var check = new ExecutoryDocumentWithholdingCapCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(1);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_OverCap_ProducesFindingWithOverflow()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var row = NewRow(owed: 1000m, withheld: 1250m);
        db.ExecutoryDocuments.Add(row);
        await db.SaveChangesAsync();

        var check = new ExecutoryDocumentWithholdingCapCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(1);
        result.Findings.Should().HaveCount(1);
        var finding = result.Findings[0];
        finding.CheckCode.Should().Be("EXECUTORY_DOC.WITHHOLDING_OVERFLOW");
        finding.Severity.Should().Be(IntegrityFindingSeverity.High);
        finding.AggregateRowId.Should().Be(row.Id);
        finding.ActualValue.Should().Contain("OverflowDelta=250");
    }

    [Fact]
    public async Task RunAsync_OpenEndedObligation_Skipped()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        db.ExecutoryDocuments.Add(NewRow(owed: null, withheld: 5000m));
        await db.SaveChangesAsync();

        var check = new ExecutoryDocumentWithholdingCapCheck();
        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        // Rows with no cap are excluded by the WHERE clause so RowsScanned=0.
        result.RowsScanned.Should().Be(0);
        result.Findings.Should().BeEmpty();
    }
}
