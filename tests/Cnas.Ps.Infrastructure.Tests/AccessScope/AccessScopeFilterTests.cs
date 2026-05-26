using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — vertical-slice tests for
/// <see cref="AccessScopeFilter"/>. Exercises the four <c>Apply…</c> methods against
/// an InMemory EF context so the predicate translation is end-to-end correct.
/// </summary>
public sealed class AccessScopeFilterTests
{
    /// <summary>Single-element CHIS scope used by the region-axis tests.</summary>
    private static readonly string[] ChisOnly = ["CHIS"];

    /// <summary>Expected display-name set for the CHIS filter test.</summary>
    private static readonly string[] ExpectedAandC = ["A", "C"];

    /// <summary>Expected names for the NULL-region tolerance test.</summary>
    private static readonly string[] ExpectedNullRegionVisible = ["Unmarked", "Chis"];

    /// <summary>Single-element CHISINAU-CENTRU subdivision scope.</summary>
    private static readonly string[] CentruOnly = ["CHISINAU-CENTRU"];

    /// <summary>Expected solicitant-ids surviving the subdivision filter.</summary>
    private static readonly long[] ExpectedSubdivisionIds = [1L, 3L];

    /// <summary>Single-element pension workflow-category scope.</summary>
    private static readonly string[] PensionOnly = ["pension"];

    /// <summary>Expected workflow-task titles surviving the workflow-category filter.</summary>
    private static readonly string[] ExpectedWorkflowTitles = ["T-Pension", "T-Unanchored"];

    /// <summary>Single-element Decision document-category scope.</summary>
    private static readonly string[] DecisionOnly = ["Decision"];

    /// <summary>Expected document titles surviving the document-category filter.</summary>
    private static readonly string[] ExpectedDocumentTitles = ["D-Decision"];

    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-accessscope-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds a scope envelope with only the supplied dimension populated; useful for
    /// tests that focus on a single axis.
    /// </summary>
    private static IAccessScope ScopeWith(
        string[]? regions = null,
        string[]? subdivisions = null,
        string[]? documentCategories = null,
        string[]? workflowCategories = null) =>
        new RolesBasedAccessScope(
            regions ?? Array.Empty<string>(),
            subdivisions ?? Array.Empty<string>(),
            documentCategories ?? Array.Empty<string>(),
            workflowCategories ?? Array.Empty<string>());

    // ───────── Solicitant ─────────

    /// <summary>
    /// A scope with AllowedRegions = ["CHIS"] filters out rows whose RegionCode is
    /// any other value — the core positive case for the region axis.
    /// </summary>
    [Fact]
    public async Task ApplyToSolicitants_WithRegionScope_FiltersOutOtherRegions()
    {
        await using var db = CreateContext();
        db.Solicitants.AddRange(
            new Solicitant { NationalId = "1", NationalIdHash = "h1", DisplayName = "A", RegionCode = "CHIS" },
            new Solicitant { NationalId = "2", NationalIdHash = "h2", DisplayName = "B", RegionCode = "BLT" },
            new Solicitant { NationalId = "3", NationalIdHash = "h3", DisplayName = "C", RegionCode = "CHIS" });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToSolicitants(db.Solicitants, ScopeWith(regions: ChisOnly));
        var names = await query.Select(s => s.DisplayName).OrderBy(n => n).ToListAsync();

        names.Should().BeEquivalentTo(ExpectedAandC);
    }

    /// <summary>
    /// An empty AllowedRegions set means "no narrowing" — the filter returns the
    /// source queryable unchanged. Hot path for the national administrator.
    /// </summary>
    [Fact]
    public async Task ApplyToSolicitants_WithEmptyAllowedRegions_ReturnsSourceUnchanged()
    {
        await using var db = CreateContext();
        db.Solicitants.AddRange(
            new Solicitant { NationalId = "1", NationalIdHash = "h1", DisplayName = "A", RegionCode = "CHIS" },
            new Solicitant { NationalId = "2", NationalIdHash = "h2", DisplayName = "B", RegionCode = "BLT" });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToSolicitants(db.Solicitants, ScopeWith());
        var rows = await query.ToListAsync();

        rows.Should().HaveCount(2);
    }

    /// <summary>
    /// Rows whose RegionCode is itself NULL remain visible to a scoped caller —
    /// the documented NULL-data semantics on <see cref="IAccessScope"/>. Asserts the
    /// design choice explicitly so a future tightening of the predicate does not
    /// accidentally remove unmarked national data from every staff grid.
    /// </summary>
    [Fact]
    public async Task ApplyToSolicitants_RowWithNullRegion_IsVisibleToScopedCaller()
    {
        await using var db = CreateContext();
        db.Solicitants.AddRange(
            new Solicitant { NationalId = "1", NationalIdHash = "h1", DisplayName = "Unmarked", RegionCode = null },
            new Solicitant { NationalId = "2", NationalIdHash = "h2", DisplayName = "Chis", RegionCode = "CHIS" },
            new Solicitant { NationalId = "3", NationalIdHash = "h3", DisplayName = "Blt", RegionCode = "BLT" });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToSolicitants(db.Solicitants, ScopeWith(regions: ChisOnly));
        var names = await query.Select(s => s.DisplayName).OrderBy(n => n).ToListAsync();

        // Both the unmarked row and the CHIS row survive — only the BLT row is hidden.
        names.Should().BeEquivalentTo(ExpectedNullRegionVisible);
    }

    // ───────── ServiceApplication ─────────

    /// <summary>
    /// A scope with AllowedSubdivisionCodes = ["CHISINAU-CENTRU"] retains only
    /// applications routed to that branch (plus NULL rows per the NULL-data rule).
    /// </summary>
    [Fact]
    public async Task ApplyToServiceApplications_WithSubdivisionScope_FiltersByBranch()
    {
        await using var db = CreateContext();
        db.Applications.AddRange(
            new ServiceApplication { SolicitantId = 1, ServicePassportId = 1, FormPayloadJson = "{}", SubdivisionCode = "CHISINAU-CENTRU" },
            new ServiceApplication { SolicitantId = 2, ServicePassportId = 1, FormPayloadJson = "{}", SubdivisionCode = "BALTI" },
            new ServiceApplication { SolicitantId = 3, ServicePassportId = 1, FormPayloadJson = "{}", SubdivisionCode = null });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToServiceApplications(db.Applications, ScopeWith(subdivisions: CentruOnly));
        var ids = await query.Select(a => a.SolicitantId).OrderBy(i => i).ToListAsync();

        // SolicitantId 1 = CHISINAU-CENTRU (visible), 3 = NULL (visible), 2 = BALTI (hidden).
        ids.Should().BeEquivalentTo(ExpectedSubdivisionIds);
    }

    // ───────── WorkflowTask ─────────

    /// <summary>
    /// A scope with AllowedWorkflowCategories = ["pension"] keeps tasks anchored to
    /// pension workflows; tasks anchored to other categories are hidden.
    /// </summary>
    [Fact]
    public async Task ApplyToWorkflowTasks_WithWorkflowCategoryScope_FiltersByParentCategory()
    {
        await using var db = CreateContext();
        // Seed two definitions: pension (categorised) + indemnization (other category).
        var pension = new WorkflowDefinition { Code = "WF-PENSION", Version = 1, DefinitionJson = "{}", IsCurrent = true, CategoryCode = "pension" };
        var indem = new WorkflowDefinition { Code = "WF-INDEMN", Version = 1, DefinitionJson = "{}", IsCurrent = true, CategoryCode = "indemnization" };
        db.WorkflowDefinitions.AddRange(pension, indem);
        await db.SaveChangesAsync();
        db.WorkflowTasks.AddRange(
            new WorkflowTask { DossierId = 100, Title = "T-Pension", NodeCode = "WF-PENSION" },
            new WorkflowTask { DossierId = 100, Title = "T-Indemn", NodeCode = "WF-INDEMN" },
            new WorkflowTask { DossierId = 100, Title = "T-Unanchored", NodeCode = null });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToWorkflowTasks(
            db.WorkflowTasks,
            ScopeWith(workflowCategories: PensionOnly),
            db.WorkflowDefinitions);
        var titles = await query.Select(t => t.Title).OrderBy(t => t).ToListAsync();

        // Pension task + unanchored task survive; indemnization task is hidden.
        titles.Should().BeEquivalentTo(ExpectedWorkflowTitles);
    }

    // ───────── Document ─────────

    /// <summary>
    /// A scope with AllowedDocumentCategories = ["Decision"] keeps only documents
    /// whose <see cref="Document.Kind"/> matches the enum name.
    /// </summary>
    [Fact]
    public async Task ApplyToDocuments_WithDocumentCategoryScope_FiltersByKind()
    {
        await using var db = CreateContext();
        db.Documents.AddRange(
            new Document { Title = "D-Decision", MimeType = "application/pdf", StorageObjectKey = "k1", StorageBucket = "b", ContentSha256Hex = "h", Kind = DocumentKind.Decision },
            new Document { Title = "D-Attachment", MimeType = "application/pdf", StorageObjectKey = "k2", StorageBucket = "b", ContentSha256Hex = "h", Kind = DocumentKind.Attachment },
            new Document { Title = "D-Cert", MimeType = "application/pdf", StorageObjectKey = "k3", StorageBucket = "b", ContentSha256Hex = "h", Kind = DocumentKind.Certificate });
        await db.SaveChangesAsync();

        var filter = new AccessScopeFilter();
        var query = filter.ApplyToDocuments(db.Documents, ScopeWith(documentCategories: DecisionOnly));
        var titles = await query.Select(d => d.Title).OrderBy(t => t).ToListAsync();

        titles.Should().BeEquivalentTo(ExpectedDocumentTitles);
    }
}
