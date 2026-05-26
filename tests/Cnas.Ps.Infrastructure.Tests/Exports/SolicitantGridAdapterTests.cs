using Cnas.Ps.Application.Exports;
using Cnas.Ps.Core.Common;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Exports;

/// <summary>
/// R0226 / TOR UI 013 — adapter tests covering the PII-redaction contract,
/// Sqid encoding of the Code column, hash-truncation safety net, and the
/// locale-aware header table.
/// </summary>
public sealed class SolicitantGridAdapterTests
{
    /// <summary>Substitute Sqid encoder that produces stable, asserting-friendly strings.</summary>
    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return s;
    }

    /// <summary>Sample source row used across most assertions.</summary>
    private static SolicitantGridRow SampleRow() => new(
        Id: 42,
        NationalIdHash: "abcd1234EFGHwxyz0987",
        DisplayName: "Ion Popescu",
        Kind: "NaturalPerson",
        CreatedAtUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
        IsActive: true);

    [Fact]
    public void Columns_DefaultLanguage_EmitsSixColumnsInOrder()
    {
        var sut = new SolicitantGridAdapter();

        var cols = sut.Columns("ro");

        cols.Should().HaveCount(6);
        cols.Select(c => c.FieldName).Should().Equal(
            "Code", "NationalIdHash", "Name", "Kind", "CreatedAtUtc", "Status");
    }

    [Fact]
    public void ToRow_WithoutPiiPermission_MasksNameColumn()
    {
        var sut = new SolicitantGridAdapter();

        var row = sut.ToRow(SampleRow(), NewSqids(), canViewPii: false);

        row.Cells["Name"].Should().Be(SolicitantGridAdapter.MaskedSentinel);
    }

    [Fact]
    public void ToRow_WithPiiPermission_PreservesName()
    {
        var sut = new SolicitantGridAdapter();

        var row = sut.ToRow(SampleRow(), NewSqids(), canViewPii: true);

        row.Cells["Name"].Should().Be("Ion Popescu");
    }

    [Fact]
    public void ToRow_AlwaysEncodesIdAsSqid_InCodeColumn()
    {
        // RULE 3 — raw long must never appear in an outward-facing payload.
        var sut = new SolicitantGridAdapter();

        var row = sut.ToRow(SampleRow(), NewSqids(), canViewPii: true);

        row.Cells["Code"].Should().Be("SQID-42");
        row.Cells["Code"].Should().NotBe(42L);
    }

    [Fact]
    public void ToRow_TruncatesHashToFirstEightCharsPlusEllipsis()
    {
        // Defense in depth: even with PII access we don't leak the full deterministic hash.
        var sut = new SolicitantGridAdapter();

        var row = sut.ToRow(SampleRow(), NewSqids(), canViewPii: true);

        var hash = row.Cells["NationalIdHash"].Should().BeOfType<string>().Subject;
        hash.Should().StartWith("abcd1234");
        hash.Should().EndWith(SolicitantGridAdapter.HashEllipsis);
        hash.Should().NotContain("EFGHwxyz0987");
    }

    [Fact]
    public void ToRow_InactiveSolicitant_StatusInactive()
    {
        var sut = new SolicitantGridAdapter();
        var inactive = SampleRow() with { IsActive = false };

        var row = sut.ToRow(inactive, NewSqids(), canViewPii: true);

        row.Cells["Status"].Should().Be("Inactive");
    }
}
