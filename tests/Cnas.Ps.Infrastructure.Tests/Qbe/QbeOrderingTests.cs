using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Qbe;

namespace Cnas.Ps.Infrastructure.Tests.Qbe;

/// <summary>
/// R0523 / TOR CF 03.05 — user-defined ordering extension to the QBE primitive
/// (<see cref="QbeToLinqConverter"/>). Verifies the <c>ApplyOrdering</c> entry-point
/// builds the correct <c>OrderBy</c>/<c>ThenBy</c> chain for one or more fields and
/// rejects fields that are not on the registry schema.
/// </summary>
public sealed class QbeOrderingTests
{
    /// <summary>Builds the converter wired against the production schema provider.</summary>
    private static QbeToLinqConverter NewConverter() => new(new QbeRegistrySchemaProvider());

    /// <summary>Builds a fixed list of <see cref="Solicitant"/> aggregates for ordering tests.</summary>
    private static List<Solicitant> SeedSolicitants() => new()
    {
        new Solicitant
        {
            Id = 3,
            NationalId = "2000000000003", NationalIdHash = "HASH-C", Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Maria Iordache",
            CreatedAtUtc = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
        new Solicitant
        {
            Id = 1,
            NationalId = "2000000000001", NationalIdHash = "HASH-A", Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu",
            CreatedAtUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
        new Solicitant
        {
            Id = 2,
            NationalId = "2000000000002", NationalIdHash = "HASH-B", Kind = ApplicantKind.LegalPerson,
            DisplayName = "Ion Popescu", // duplicate name on purpose so secondary key matters
            CreatedAtUtc = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
    };

    [Fact]
    public void ApplyOrdering_SingleFieldAscending_OrdersByThatField()
    {
        var sut = NewConverter();
        var source = SeedSolicitants().AsQueryable();
        var orderings = new[] { new QbeOrdering("DisplayName", QbeSortDirection.Asc) };

        var result = sut.ApplyOrdering<Solicitant>(source, "Solicitant", orderings);

        result.IsSuccess.Should().BeTrue();
        var ordered = result.Value.ToList();
        ordered.Select(s => s.DisplayName).Should().ContainInOrder("Ion Popescu", "Ion Popescu", "Maria Iordache");
    }

    [Fact]
    public void ApplyOrdering_MultiField_NameAscThenCreatedDesc_RespectsSecondary()
    {
        var sut = NewConverter();
        var source = SeedSolicitants().AsQueryable();
        var orderings = new[]
        {
            new QbeOrdering("DisplayName", QbeSortDirection.Asc),
            new QbeOrdering("CreatedAtUtc", QbeSortDirection.Desc),
        };

        var result = sut.ApplyOrdering<Solicitant>(source, "Solicitant", orderings);

        result.IsSuccess.Should().BeTrue();
        var ordered = result.Value.ToList();
        // The two "Ion Popescu" rows are tied on the primary key; the secondary DESC
        // key on CreatedAtUtc must place the 2026-02-15 row BEFORE the 2026-01-05 row.
        ordered[0].Id.Should().Be(2);
        ordered[1].Id.Should().Be(1);
        ordered[2].Id.Should().Be(3);
    }

    [Fact]
    public void ApplyOrdering_UnknownField_ReturnsQbeFieldNotQueryableFailure()
    {
        var sut = NewConverter();
        var source = SeedSolicitants().AsQueryable();
        var orderings = new[] { new QbeOrdering("NotAColumn", QbeSortDirection.Asc) };

        var result = sut.ApplyOrdering<Solicitant>(source, "Solicitant", orderings);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeFieldNotQueryable);
    }
}
