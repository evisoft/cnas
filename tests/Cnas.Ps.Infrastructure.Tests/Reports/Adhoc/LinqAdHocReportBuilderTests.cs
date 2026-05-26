using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Reports.Adhoc;

/// <summary>
/// R0580 / TOR CF 09.02 — unit tests for the ad-hoc LINQ report builder.
/// Exercises each supported entity set, the filter operators, and the
/// row-cap guard.
/// </summary>
public sealed class LinqAdHocReportBuilderTests
{
    /// <summary>Deterministic UTC clock instant.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Reused column projections (CA1861 — no inline new[] arrays).</summary>
    private static readonly string[] AppRefAndStatus = ["ReferenceNumber", "Status"];

    /// <summary>Reused column projections.</summary>
    private static readonly string[] ContribIdnoDenumire = ["Idno", "Denumire"];

    /// <summary>Reused column projections.</summary>
    private static readonly string[] DossierNumberOnly = ["DossierNumber"];

    /// <summary>Reused column projection containing an unknown column.</summary>
    private static readonly string[] AppRefAndUnknown = ["ReferenceNumber", "NotAColumn"];

    /// <summary>Empty filter list.</summary>
    private static readonly AdHocReportFilterDto[] NoFilters = Array.Empty<AdHocReportFilterDto>();

    /// <summary>Single Id-column projection.</summary>
    private static readonly string[] IdOnly = ["Id"];

    /// <summary>Applications entity set returns rows with the requested columns.</summary>
    [Fact]
    public async Task BuildAsync_Applications_ReturnsRequestedColumns()
    {
        var h = await Harness.CreateAsync();
        h.Db.Applications.Add(NewApp("REF-001", ApplicationStatus.Submitted));
        h.Db.Applications.Add(NewApp("REF-002", ApplicationStatus.Approved));
        await h.Db.SaveChangesAsync();

        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            AppRefAndStatus,
            NoFilters,
            OrderBy: "ReferenceNumber",
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().HaveCount(2);
        result.Value.Rows[0]["ReferenceNumber"].Should().Be("REF-001");
        result.Value.Rows[1]["ReferenceNumber"].Should().Be("REF-002");
    }

    /// <summary>Contributors entity set + EQ filter narrows to a single row.</summary>
    [Fact]
    public async Task BuildAsync_Contributors_WithEqFilter_NarrowsRows()
    {
        var h = await Harness.CreateAsync();
        h.Db.Contributors.Add(NewContributor("1000000000017", "Alpha Co"));
        h.Db.Contributors.Add(NewContributor("1000000000025", "Beta SRL"));
        await h.Db.SaveChangesAsync();

        var filters = new[]
        {
            new AdHocReportFilterDto("Denumire", AdHocReportOperators.Eq, "Alpha Co"),
        };
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Contributors,
            ContribIdnoDenumire,
            filters,
            OrderBy: null,
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().HaveCount(1);
        result.Value.Rows[0]["Idno"].Should().Be("1000000000017");
    }

    /// <summary>Dossiers entity set + CONTAINS filter matches substrings.</summary>
    [Fact]
    public async Task BuildAsync_Dossiers_WithContainsFilter_MatchesSubstring()
    {
        var h = await Harness.CreateAsync();
        var app = NewApp("REF-001", ApplicationStatus.Approved);
        h.Db.Applications.Add(app);
        await h.Db.SaveChangesAsync();
        h.Db.Dossiers.Add(new Dossier
        {
            ApplicationId = app.Id,
            DossierNumber = "D-FOO-001",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        h.Db.Dossiers.Add(new Dossier
        {
            ApplicationId = app.Id,
            DossierNumber = "D-BAR-002",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        var filters = new[]
        {
            new AdHocReportFilterDto("DossierNumber", AdHocReportOperators.Contains, "FOO"),
        };
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Dossiers,
            DossierNumberOnly,
            filters,
            OrderBy: null,
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().HaveCount(1);
        result.Value.Rows[0]["DossierNumber"].Should().Be("D-FOO-001");
    }

    /// <summary>
    /// Decisions entity set filters down to Approved / Rejected applications.
    /// </summary>
    [Fact]
    public async Task BuildAsync_Decisions_OnlyApprovedOrRejectedRows()
    {
        var h = await Harness.CreateAsync();
        h.Db.Applications.Add(NewApp("REF-A", ApplicationStatus.Approved));
        h.Db.Applications.Add(NewApp("REF-R", ApplicationStatus.Rejected));
        h.Db.Applications.Add(NewApp("REF-S", ApplicationStatus.Submitted));
        await h.Db.SaveChangesAsync();

        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Decisions,
            AppRefAndStatus,
            NoFilters,
            OrderBy: "ReferenceNumber",
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().HaveCount(2,
            "only Approved / Rejected applications participate in the Decisions registry.");
    }

    /// <summary>Unknown column on a valid entity set returns
    /// <see cref="ErrorCodes.AdHocReportUnknownColumn"/>.</summary>
    [Fact]
    public async Task BuildAsync_UnknownColumn_Fails()
    {
        var h = await Harness.CreateAsync();
        var spec = new AdHocReportSpecDto(
            AdHocReportEntitySets.Applications,
            AppRefAndUnknown,
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AdHocReportUnknownColumn);
    }

    /// <summary>Unknown entity set returns
    /// <see cref="ErrorCodes.ValidationFailed"/> (validator rejects it first).</summary>
    [Fact]
    public async Task BuildAsync_UnknownEntitySet_Fails()
    {
        var h = await Harness.CreateAsync();
        var spec = new AdHocReportSpecDto(
            "NotARealEntity",
            IdOnly,
            NoFilters,
            OrderBy: null,
            Descending: false);

        var result = await h.Builder.BuildAsync(spec);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    private static ServiceApplication NewApp(string refNumber, ApplicationStatus status) => new()
    {
        SolicitantId = 1,
        ServicePassportId = 1,
        ReferenceNumber = refNumber,
        Status = status,
        FormPayloadJson = "{}",
        SubmittedAtUtc = ClockNow,
        CreatedAtUtc = ClockNow,
        IsActive = true,
    };

    private static Contributor NewContributor(string idno, string name) => new()
    {
        Idno = idno,
        IdnoHash = $"h-{idno}",
        Denumire = name,
        CreatedAtUtc = ClockNow,
        IsActive = true,
    };

    /// <summary>Harness for the SUT.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required LinqAdHocReportBuilder Builder { get; init; }

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-adhoc-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            IReadOnlyCnasDbContext readDb = db;
            var validator = new AdHocReportSpecValidator();
            var builder = new LinqAdHocReportBuilder(readDb, validator);
            return Task.FromResult(new Harness { Db = db, Builder = builder });
        }
    }
}
