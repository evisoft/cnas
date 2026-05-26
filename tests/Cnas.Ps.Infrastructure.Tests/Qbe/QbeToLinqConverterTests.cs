using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Qbe;

namespace Cnas.Ps.Infrastructure.Tests.Qbe;

/// <summary>
/// R0163 — unit tests for <see cref="QbeToLinqConverter"/>. The tests build the predicate
/// and execute it against an in-memory list rather than EF Core, so the assertions stay
/// focused on the expression-tree shape rather than on EF's translation pipeline.
/// </summary>
public sealed class QbeToLinqConverterTests
{
    /// <summary>Builds the converter wired against the production schema provider.</summary>
    private static QbeToLinqConverter NewConverter() =>
        new(new QbeRegistrySchemaProvider());

    /// <summary>Builds a list of <see cref="Solicitant"/> aggregates for predicate-evaluation tests.</summary>
    private static List<Solicitant> SeedSolicitants() => new()
    {
        new Solicitant
        {
            NationalId = "2000000000001", NationalIdHash = "HASH-A", Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu", Email = "ion@example.com", PhoneE164 = "+37360111111",
            CreatedAtUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
        new Solicitant
        {
            NationalId = "2000000000002", NationalIdHash = "HASH-B", Kind = ApplicantKind.LegalPerson,
            DisplayName = "SRL Cărbune", Email = "office@carbune.md", PhoneE164 = "+37360222222",
            CreatedAtUtc = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
        new Solicitant
        {
            NationalId = "2000000000003", NationalIdHash = "HASH-C", Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Maria Iordache", Email = null, PhoneE164 = "+37360333333",
            CreatedAtUtc = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        },
    };

    [Fact]
    public void Convert_StringEquals_BuildsPredicate_AndFiltersInMemory()
    {
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("Email", QbeOperator.Equals, "ion@example.com"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsSuccess.Should().BeTrue();
        var pred = result.Value.Compile();
        SeedSolicitants().Where(pred).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ion Popescu");
    }

    [Fact]
    public void Convert_StringContains_CaseInsensitive_MatchesSubstring()
    {
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("DisplayName", QbeOperator.Contains, "POPESCU"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsSuccess.Should().BeTrue();
        var pred = result.Value.Compile();
        SeedSolicitants().Where(pred).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ion Popescu");
    }

    [Fact]
    public void Convert_StringContains_WithStarMask_TreatedAsPrefixOrSuffix()
    {
        var sut = NewConverter();
        // "Ion*" with explicit trailing star → anchored starts-with semantics via
        // WildcardMask.ToRegex. Only "Ion Popescu" starts with "Ion" in the seed.
        var starsuffix = new QbeFilter("AND", new[]
        {
            new QbeCondition("DisplayName", QbeOperator.Contains, "Ion*"),
        });
        var matchesStartsWith = sut.Convert<Solicitant>("Solicitant", starsuffix);
        matchesStartsWith.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(matchesStartsWith.Value.Compile()).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ion Popescu");

        // "*pescu" with explicit leading star → anchored ends-with semantics. Only
        // "Ion Popescu" ends with "pescu" in the seed.
        var starprefix = new QbeFilter("AND", new[]
        {
            new QbeCondition("DisplayName", QbeOperator.Contains, "*pescu"),
        });
        var matchesEndsWith = sut.Convert<Solicitant>("Solicitant", starprefix);
        matchesEndsWith.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(matchesEndsWith.Value.Compile()).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Ion Popescu");

        // "*cărbune*" → both sides anchored → substring match. Case-insensitive default
        // matches "Cărbune" inside "SRL Cărbune".
        var bothside = new QbeFilter("AND", new[]
        {
            new QbeCondition("DisplayName", QbeOperator.Contains, "*cărbune*"),
        });
        var matchesBoth = sut.Convert<Solicitant>("Solicitant", bothside);
        matchesBoth.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(matchesBoth.Value.Compile()).Should().ContainSingle()
            .Which.DisplayName.Should().Be("SRL Cărbune");
    }

    [Fact]
    public void Convert_DateTimeBetween_MatchesInclusiveRange()
    {
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("CreatedAtUtc", QbeOperator.Between,
                Value: "2026-02-01T00:00:00Z", Value2: "2026-03-01T00:00:00Z"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsSuccess.Should().BeTrue();
        var matched = SeedSolicitants().Where(result.Value.Compile()).ToList();
        matched.Should().ContainSingle().Which.DisplayName.Should().Be("SRL Cărbune");
    }

    [Fact]
    public void Convert_InOperator_OnEnumField_MatchesEveryListedValue()
    {
        // Field 'Kind' is ApplicantKind enum — In list values must match enum NAMES
        // case-insensitively. The seed has 2 NaturalPerson + 1 LegalPerson rows; "In"
        // with both names should match all 3.
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("Kind", QbeOperator.In, "NaturalPerson,LegalPerson"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(result.Value.Compile()).Should().HaveCount(3);
    }

    [Fact]
    public void Convert_IsNullAndIsNotNull_OnNullableStringField()
    {
        var sut = NewConverter();
        var isNullFilter = new QbeFilter("AND", new[]
        {
            new QbeCondition("Email", QbeOperator.IsNull, null),
        });
        var isNullResult = sut.Convert<Solicitant>("Solicitant", isNullFilter);
        isNullResult.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(isNullResult.Value.Compile()).Should().ContainSingle()
            .Which.DisplayName.Should().Be("Maria Iordache");

        var isNotNullFilter = new QbeFilter("AND", new[]
        {
            new QbeCondition("Email", QbeOperator.IsNotNull, null),
        });
        var isNotNullResult = sut.Convert<Solicitant>("Solicitant", isNotNullFilter);
        isNotNullResult.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(isNotNullResult.Value.Compile()).Should().HaveCount(2);
    }

    [Fact]
    public void Convert_FieldNotInSchema_ReturnsQbeFieldNotQueryable()
    {
        // PreferredLanguage is queryable; but a misspelled "PreferedLanguage" must be
        // rejected up-front — a permissive converter would let a hostile caller probe
        // entity columns that are not on the allow-list.
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("InsuredId", QbeOperator.Equals, "x"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeFieldNotQueryable);
    }

    [Fact]
    public void Convert_BetweenOnBoolField_ReturnsQbeOperatorNotSupported()
    {
        // bool fields support Equals/NotEquals/In only — Between is type-incompatible.
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("IsActive", QbeOperator.Between, "true", "false"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeOperatorNotSupported);
    }

    [Fact]
    public void Convert_CaseSensitiveField_RespectsCaseInEquality()
    {
        // NationalIdHash is declared with IsCaseSensitive=true. Uppercase HASH-A is in
        // seed; lowercase "hash-a" must NOT match.
        var sut = NewConverter();
        var exact = new QbeFilter("AND", new[]
        {
            new QbeCondition("NationalIdHash", QbeOperator.Equals, "HASH-A"),
        });
        var exactRes = sut.Convert<Solicitant>("Solicitant", exact);
        exactRes.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(exactRes.Value.Compile()).Should().ContainSingle();

        var wrongCase = new QbeFilter("AND", new[]
        {
            new QbeCondition("NationalIdHash", QbeOperator.Equals, "hash-a"),
        });
        var wrongRes = sut.Convert<Solicitant>("Solicitant", wrongCase);
        wrongRes.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(wrongRes.Value.Compile()).Should().BeEmpty();
    }

    [Fact]
    public void Convert_OrCombinator_UnionsConditions()
    {
        // Two conditions joined by OR: Email exact match (1 row) UNION DisplayName starts
        // with "Maria" (1 row, different one) → 2 rows.
        var sut = NewConverter();
        var filter = new QbeFilter("OR", new[]
        {
            new QbeCondition("Email", QbeOperator.Equals, "ion@example.com"),
            new QbeCondition("DisplayName", QbeOperator.StartsWith, "Maria"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsSuccess.Should().BeTrue();
        var matched = SeedSolicitants().Where(result.Value.Compile()).ToList();
        matched.Should().HaveCount(2);
        // AND would have produced 0 rows (no row satisfies both) — the OR union is the contract.
        var andFilter = new QbeFilter("AND", filter.Conditions);
        var andRes = sut.Convert<Solicitant>("Solicitant", andFilter);
        andRes.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(andRes.Value.Compile()).Should().BeEmpty();
    }

    [Fact]
    public void Convert_UnknownRegistry_ReturnsQbeRegistryUnknown()
    {
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("Id", QbeOperator.Equals, "1"),
        });

        var result = sut.Convert<Solicitant>("DoesNotExist", filter);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeRegistryUnknown);
    }

    [Fact]
    public void Convert_NullOrEmptyFilter_BuildsTautology()
    {
        // Empty filter must short-circuit to "match all" — the predicate must compile
        // and return true for every row.
        var sut = NewConverter();
        var result = sut.Convert<Solicitant>("Solicitant", filter: null);

        result.IsSuccess.Should().BeTrue();
        SeedSolicitants().Where(result.Value.Compile()).Should().HaveCount(3);
    }

    [Fact]
    public void Convert_InvalidBoolValue_ReturnsQbeValueInvalid()
    {
        // Parsing should reject non-bool strings on a bool field.
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("IsActive", QbeOperator.Equals, "maybe"),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeValueInvalid);
    }

    [Fact]
    public void Convert_BetweenWithMissingValue2_ReturnsQbeValueInvalid()
    {
        var sut = NewConverter();
        var filter = new QbeFilter("AND", new[]
        {
            new QbeCondition("CreatedAtUtc", QbeOperator.Between, "2026-02-01T00:00:00Z", Value2: null),
        });

        var result = sut.Convert<Solicitant>("Solicitant", filter);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QbeValueInvalid);
    }
}
