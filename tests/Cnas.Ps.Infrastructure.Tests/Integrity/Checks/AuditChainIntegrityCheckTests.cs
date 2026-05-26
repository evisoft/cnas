using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Integrity.Checks;

/// <summary>
/// R2709 / TOR SEC 053 — invariant tests for
/// <see cref="AuditChainIntegrityCheck"/>. The check wraps
/// <see cref="IAuditChainVerifier"/> and converts a broken-chain report into
/// a Critical <c>IntegrityCheckFindingRecord</c>. The verifier dependency is
/// substituted with NSubstitute so each test can pin the exact report shape.
/// </summary>
public sealed class AuditChainIntegrityCheckTests
{
    /// <summary>Builds a verifier substitute that returns the supplied report.</summary>
    /// <param name="report">Canned report.</param>
    /// <returns>Configured NSubstitute mock.</returns>
    private static IAuditChainVerifier NewVerifier(AuditChainVerificationReport report)
    {
        var verifier = Substitute.For<IAuditChainVerifier>();
        verifier
            .VerifyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditChainVerificationReport>.Success(report)));
        return verifier;
    }

    [Fact]
    public async Task RunAsync_ChainIntact_ReturnsNoFindingsButCountsRowsScanned()
    {
        // R0194 happy path: IsValid=true, CheckedCount=42, no broken row.
        var verifier = NewVerifier(new AuditChainVerificationReport(
            IsValid: true,
            CheckedCount: 42,
            FirstBrokenRowId: null,
            FirstBrokenReason: null));

        var check = new AuditChainIntegrityCheck(verifier);
        using var db = IntegrityTestHelpers.CreateContext();

        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(42);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_PrevHashMismatch_ProducesSingleCriticalFinding()
    {
        var verifier = NewVerifier(new AuditChainVerificationReport(
            IsValid: false,
            CheckedCount: 5,
            FirstBrokenRowId: 17L,
            FirstBrokenReason: "PrevHashMismatch"));

        var check = new AuditChainIntegrityCheck(verifier);
        using var db = IntegrityTestHelpers.CreateContext();

        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(5);
        result.Findings.Should().HaveCount(1);
        var finding = result.Findings[0];
        finding.CheckCode.Should().Be("AUDIT_LOG.CHAIN");
        finding.Severity.Should().Be(IntegrityFindingSeverity.Critical);
        finding.AggregateName.Should().Be("AuditLog");
        finding.AggregateRowId.Should().Be(17L);
        finding.Description.Should().Contain("PrevHashMismatch");
        finding.Description.Should().NotContain("DetailsJson");
    }

    [Fact]
    public async Task RunAsync_RowHashMismatch_ProducesSingleCriticalFinding()
    {
        var verifier = NewVerifier(new AuditChainVerificationReport(
            IsValid: false,
            CheckedCount: 9,
            FirstBrokenRowId: 91L,
            FirstBrokenReason: "RowHashMismatch"));

        var check = new AuditChainIntegrityCheck(verifier);
        using var db = IntegrityTestHelpers.CreateContext();

        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(9);
        result.Findings.Should().HaveCount(1);
        var finding = result.Findings[0];
        finding.CheckCode.Should().Be("AUDIT_LOG.CHAIN");
        finding.Severity.Should().Be(IntegrityFindingSeverity.Critical);
        finding.AggregateRowId.Should().Be(91L);
        finding.Description.Should().Contain("RowHashMismatch");
    }

    [Fact]
    public async Task RunAsync_VerifierFailsTechnical_ReturnsZeroScanAndOneFinding()
    {
        // When the verifier itself errors (e.g. the read context is unreachable),
        // we still want operators to see SOMETHING in the integrity sweep so they
        // can investigate — emit one critical finding describing the failure.
        var verifier = Substitute.For<IAuditChainVerifier>();
        verifier
            .VerifyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditChainVerificationReport>.Failure(
                "AUDIT.VERIFY_FAILED", "reader unreachable")));

        var check = new AuditChainIntegrityCheck(verifier);
        using var db = IntegrityTestHelpers.CreateContext();

        var result = await check.RunAsync(IntegrityTestHelpers.WrapContext(db), CancellationToken.None);

        result.RowsScanned.Should().Be(0);
        result.Findings.Should().HaveCount(1);
        result.Findings[0].Severity.Should().Be(IntegrityFindingSeverity.Critical);
        result.Findings[0].CheckCode.Should().Be("AUDIT_LOG.CHAIN");
        result.Findings[0].AggregateRowId.Should().Be(0L);
    }

    [Fact]
    public void Metadata_IsStable()
    {
        var verifier = Substitute.For<IAuditChainVerifier>();
        var check = new AuditChainIntegrityCheck(verifier);

        check.CheckCode.Should().Be("AUDIT_LOG.CHAIN");
        check.AggregateName.Should().Be("AuditLog");
        check.Severity.Should().Be(IntegrityFindingSeverity.Critical);
    }
}
