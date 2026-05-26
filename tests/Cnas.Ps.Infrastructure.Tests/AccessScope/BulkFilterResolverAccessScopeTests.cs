using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 follow-up — verifies the wiring contract: the three list-shaped bulk
/// selection resolvers (<see cref="CerereFilterResolver"/>,
/// <see cref="WorkflowTaskFilterResolver"/>, <see cref="DecisionFilterResolver"/>)
/// consult <see cref="Cnas.Ps.Application.AccessScope.IAccessScopeFilter"/> BEFORE
/// returning matching row ids.
/// </summary>
/// <remarks>
/// <para>
/// These resolvers back the <c>POST /api/bulk-selections</c> endpoint — they are
/// the only "all-matching" list-shaped queryables on the ServiceApplication /
/// WorkflowTask / Decision registries. The tests exercise three properties per
/// registry:
/// </para>
/// <list type="number">
///   <item><description>A scoped caller sees only rows matching the allow-list.</description></item>
///   <item><description>An unscoped caller (national admin) sees every row.</description></item>
///   <item><description>NULL-scoped rows (national / unmarked data) are visible to every caller.</description></item>
/// </list>
/// </remarks>
public sealed class BulkFilterResolverAccessScopeTests
{
    /// <summary>Allow-list with the single CHISINAU-CENTRU subdivision.</summary>
    private static readonly string[] CentruOnly = ["CHISINAU-CENTRU"];

    /// <summary>Allow-list with a subdivision that matches no seeded row.</summary>
    private static readonly string[] ZzzSubdivision = ["ZZZ"];

    /// <summary>Allow-list with the single pension workflow category.</summary>
    private static readonly string[] PensionOnly = ["pension"];

    /// <summary>Allow-list with a category that matches no seeded workflow definition.</summary>
    private static readonly string[] ZzzCategory = ["ZZZ"];

    /// <summary>Empty axis reused everywhere CA1825 would otherwise complain.</summary>
    private static readonly string[] EmptyAxis = Array.Empty<string>();

    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> per test.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-bulk-scope-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds an empty caller stub whose AccessScope returns the supplied envelope.</summary>
    private static ICallerContext BuildCaller(IAccessScope scope)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.AccessScope.Returns(scope);
        return caller;
    }

    /// <summary>Filter envelope JSON with no per-resolver predicate populated.</summary>
    private const string EmptyFilter = "{}";

    // ─────────────────────────── CerereFilterResolver ───────────────────────────

    /// <summary>
    /// Seeds three <see cref="ServiceApplication"/> rows: one in <c>CHISINAU-CENTRU</c>,
    /// one in <c>BLT-NORD</c>, and one with <c>SubdivisionCode = null</c> (national /
    /// unmarked dossier).
    /// </summary>
    private static async Task SeedApplicationsAsync(CnasDbContext db)
    {
        // Seed the parent passport row + solicitant since ServiceApplication
        // has FK columns.
        var solicitant = new Solicitant
        {
            NationalId = "1234567890123",
            NationalIdHash = "h-bulk",
            DisplayName = "Bulk Test",
            Kind = ApplicantKind.NaturalPerson,
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        var passport = new ServicePassport
        {
            Code = "BULK",
            NameRo = "Bulk",
            DescriptionRo = "Bulk passport",
            Version = 1,
            IsCurrent = true,
            IsActive = true,
            WorkflowCode = "wf",
            FormSchemaJson = "{}",
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        db.Applications.AddRange(
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "REF-CENTRU",
                IsActive = true,
                SubdivisionCode = "CHISINAU-CENTRU",
            },
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "REF-BLT",
                IsActive = true,
                SubdivisionCode = "BLT-NORD",
            },
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "REF-NATIONAL",
                IsActive = true,
                SubdivisionCode = null,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A scoped caller (subdivision = <c>CHISINAU-CENTRU</c>) sees ONLY the
    /// matching application + the NULL-subdivision row — never the BLT-NORD row.
    /// </summary>
    [Fact]
    public async Task CerereFilterResolver_ScopedCaller_SeesOnlyScopedRows()
    {
        await using var db = CreateContext();
        await SeedApplicationsAsync(db);
        var scope = new RolesBasedAccessScope(
            EmptyAxis, CentruOnly, EmptyAxis, EmptyAxis);
        var resolver = new CerereFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        // CHISINAU-CENTRU row + NULL row = 2 of the 3 seeded.
        ids.Value.Should().HaveCount(2);
        var refs = await db.Applications.Where(a => ids.Value.Contains(a.Id))
            .Select(a => a.ReferenceNumber).ToListAsync();
        refs.Should().Contain("REF-CENTRU").And.Contain("REF-NATIONAL")
            .And.NotContain("REF-BLT");
    }

    /// <summary>
    /// An unscoped caller (national admin) sees every row — the filter is a
    /// no-op when <see cref="IAccessScope.AllowedSubdivisionCodes"/> is empty.
    /// </summary>
    [Fact]
    public async Task CerereFilterResolver_UnscopedCaller_SeesAllRows()
    {
        await using var db = CreateContext();
        await SeedApplicationsAsync(db);
        var resolver = new CerereFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(RolesBasedAccessScope.Unscoped));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(3);
    }

    /// <summary>
    /// Rows whose <see cref="ServiceApplication.SubdivisionCode"/> is <c>null</c> are
    /// visible to a caller whose allow-list does not match any seeded subdivision —
    /// the documented NULL-data semantics, exercised end-to-end through the resolver.
    /// </summary>
    [Fact]
    public async Task CerereFilterResolver_NullSubdivisionRow_IsVisibleToScopedCaller()
    {
        await using var db = CreateContext();
        await SeedApplicationsAsync(db);
        // Allow-list of "ZZZ" matches no seeded SubdivisionCode — only the NULL row survives.
        var scope = new RolesBasedAccessScope(
            EmptyAxis, ZzzSubdivision, EmptyAxis, EmptyAxis);
        var resolver = new CerereFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(1);
        var refs = await db.Applications.Where(a => ids.Value.Contains(a.Id))
            .Select(a => a.ReferenceNumber).ToListAsync();
        refs.Should().ContainSingle().And.Contain("REF-NATIONAL");
    }

    // ─────────────────────────── WorkflowTaskFilterResolver ───────────────────────────

    /// <summary>
    /// Seeds two workflow definitions (one tagged <c>pension</c>, one tagged
    /// <c>social</c>) plus three workflow tasks: one anchored to each definition
    /// and one unanchored (<see cref="WorkflowTask.NodeCode"/> = <c>null</c>).
    /// </summary>
    private static async Task SeedWorkflowTasksAsync(CnasDbContext db)
    {
        db.WorkflowDefinitions.AddRange(
            new WorkflowDefinition
            {
                Code = "WF-PENS",
                DefinitionJson = "{}",
                Version = 1,
                IsCurrent = true,
                IsActive = true,
                CategoryCode = "pension",
            },
            new WorkflowDefinition
            {
                Code = "WF-SOC",
                DefinitionJson = "{}",
                Version = 1,
                IsCurrent = true,
                IsActive = true,
                CategoryCode = "social",
            });
        await db.SaveChangesAsync();

        db.WorkflowTasks.AddRange(
            new WorkflowTask
            {
                Title = "T-Pension",
                Status = WorkflowTaskStatus.Pending,
                IsActive = true,
                NodeCode = "WF-PENS",
            },
            new WorkflowTask
            {
                Title = "T-Social",
                Status = WorkflowTaskStatus.Pending,
                IsActive = true,
                NodeCode = "WF-SOC",
            },
            new WorkflowTask
            {
                Title = "T-Unanchored",
                Status = WorkflowTaskStatus.Pending,
                IsActive = true,
                NodeCode = null,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A scoped caller (workflow category = <c>pension</c>) sees ONLY the matching
    /// task + the unanchored task — the social task is hidden.
    /// </summary>
    [Fact]
    public async Task WorkflowTaskFilterResolver_ScopedCaller_SeesOnlyScopedRows()
    {
        await using var db = CreateContext();
        await SeedWorkflowTasksAsync(db);
        var scope = new RolesBasedAccessScope(
            EmptyAxis, EmptyAxis, EmptyAxis, PensionOnly);
        var resolver = new WorkflowTaskFilterResolver(
            db, BuildSqids(), new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        // Pension task (anchored to WF-PENS / pension) + unanchored task = 2 visible.
        ids.Value.Should().HaveCount(2);
        var titles = await db.WorkflowTasks.Where(t => ids.Value.Contains(t.Id))
            .Select(t => t.Title).ToListAsync();
        titles.Should().Contain("T-Pension").And.Contain("T-Unanchored")
            .And.NotContain("T-Social");
    }

    /// <summary>
    /// An unscoped caller sees all three tasks — the filter is a no-op when
    /// <see cref="IAccessScope.AllowedWorkflowCategories"/> is empty.
    /// </summary>
    [Fact]
    public async Task WorkflowTaskFilterResolver_UnscopedCaller_SeesAllRows()
    {
        await using var db = CreateContext();
        await SeedWorkflowTasksAsync(db);
        var resolver = new WorkflowTaskFilterResolver(
            db, BuildSqids(), new AccessScopeFilter(),
            BuildCaller(RolesBasedAccessScope.Unscoped));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(3);
    }

    /// <summary>
    /// Tasks with <c>NodeCode = null</c> (legacy / unanchored data) remain visible to
    /// every scoped caller — the documented NULL-data semantics.
    /// </summary>
    [Fact]
    public async Task WorkflowTaskFilterResolver_NullNodeCodeRow_IsVisibleToScopedCaller()
    {
        await using var db = CreateContext();
        await SeedWorkflowTasksAsync(db);
        var scope = new RolesBasedAccessScope(
            EmptyAxis, EmptyAxis, EmptyAxis, ZzzCategory);
        var resolver = new WorkflowTaskFilterResolver(
            db, BuildSqids(), new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        // Allow-list "ZZZ" matches no category; only the unanchored task survives.
        ids.Value.Should().HaveCount(1);
        var titles = await db.WorkflowTasks.Where(t => ids.Value.Contains(t.Id))
            .Select(t => t.Title).ToListAsync();
        titles.Should().ContainSingle().And.Contain("T-Unanchored");
    }

    // ─────────────────────────── DecisionFilterResolver ───────────────────────────

    /// <summary>
    /// Seeds three decision-bearing applications (Approved + Rejected statuses) across
    /// two subdivisions plus one null-subdivision row.
    /// </summary>
    private static async Task SeedDecisionsAsync(CnasDbContext db)
    {
        var solicitant = new Solicitant
        {
            NationalId = "9999999999999",
            NationalIdHash = "h-dec",
            DisplayName = "Decision Test",
            Kind = ApplicantKind.NaturalPerson,
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        var passport = new ServicePassport
        {
            Code = "DEC",
            NameRo = "Decision",
            DescriptionRo = "Decision passport",
            Version = 1,
            IsCurrent = true,
            IsActive = true,
            WorkflowCode = "wf",
            FormSchemaJson = "{}",
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        db.Applications.AddRange(
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "DEC-CENTRU",
                IsActive = true,
                SubdivisionCode = "CHISINAU-CENTRU",
            },
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Rejected,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "DEC-BLT",
                IsActive = true,
                SubdivisionCode = "BLT-NORD",
            },
            new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                ReferenceNumber = "DEC-NATIONAL",
                IsActive = true,
                SubdivisionCode = null,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A scoped caller (subdivision = <c>CHISINAU-CENTRU</c>) sees only the matching
    /// decision + the NULL-subdivision decision; the BLT decision is hidden.
    /// </summary>
    [Fact]
    public async Task DecisionFilterResolver_ScopedCaller_SeesOnlyScopedRows()
    {
        await using var db = CreateContext();
        await SeedDecisionsAsync(db);
        var scope = new RolesBasedAccessScope(
            EmptyAxis, CentruOnly, EmptyAxis, EmptyAxis);
        var resolver = new DecisionFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(2);
        var refs = await db.Applications.Where(a => ids.Value.Contains(a.Id))
            .Select(a => a.ReferenceNumber).ToListAsync();
        refs.Should().Contain("DEC-CENTRU").And.Contain("DEC-NATIONAL")
            .And.NotContain("DEC-BLT");
    }

    /// <summary>
    /// An unscoped caller sees every Approved / Rejected decision.
    /// </summary>
    [Fact]
    public async Task DecisionFilterResolver_UnscopedCaller_SeesAllRows()
    {
        await using var db = CreateContext();
        await SeedDecisionsAsync(db);
        var resolver = new DecisionFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(RolesBasedAccessScope.Unscoped));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(3);
    }

    /// <summary>
    /// Decisions with <c>SubdivisionCode = null</c> remain visible to every scoped
    /// caller — preserves the documented NULL-data semantics on the decision projection.
    /// </summary>
    [Fact]
    public async Task DecisionFilterResolver_NullSubdivisionRow_IsVisibleToScopedCaller()
    {
        await using var db = CreateContext();
        await SeedDecisionsAsync(db);
        var scope = new RolesBasedAccessScope(
            EmptyAxis, ZzzSubdivision, EmptyAxis, EmptyAxis);
        var resolver = new DecisionFilterResolver(
            db, new AccessScopeFilter(), BuildCaller(scope));

        var ids = await resolver.ResolveAsync(EmptyFilter);

        ids.IsSuccess.Should().BeTrue();
        ids.Value.Should().HaveCount(1);
        var refs = await db.Applications.Where(a => ids.Value.Contains(a.Id))
            .Select(a => a.ReferenceNumber).ToListAsync();
        refs.Should().ContainSingle().And.Contain("DEC-NATIONAL");
    }

    /// <summary>NSubstitute helper — returns an <see cref="ISqidService"/> that round-trips
    /// <c>SQID-&lt;n&gt;</c> patterns so the WorkflowTask resolver can decode owner ids.</summary>
    private static ISqidService BuildSqids()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(c =>
        {
            var arg = c.Arg<string?>();
            if (!string.IsNullOrEmpty(arg)
                && arg.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(arg.AsSpan(5), out var n))
            {
                return Result<long>.Success(n);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }
}
