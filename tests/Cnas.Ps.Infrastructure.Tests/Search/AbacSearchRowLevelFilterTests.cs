using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SC = System.Security.Claims;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Search;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Search;

/// <summary>
/// R0526 / TOR CF 03.10 — tests for <see cref="AbacSearchRowLevelFilter"/>.
/// Validates the four row-level scoping behaviours:
/// (1) the configured super-role bypasses every gate;
/// (2) absent rule set defaults to deny (empty result);
/// (3) a present rule set whose default effect is Allow returns the unscoped
///     query unchanged;
/// (4) a rule set with a region-scope expression filters the projected rows
///     by the caller's <c>region_code</c> claim.
/// </summary>
public sealed class AbacSearchRowLevelFilterTests
{
    /// <summary>Deterministic clock instant — keeps audit/snapshot fields stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Pre-allocated cnas-admin role list (CA1861 — avoid constant array args).</summary>
    private static readonly string[] AdminRoles = new[] { "cnas-admin" };

    /// <summary>Pre-allocated cnas-user role list (CA1861 — avoid constant array args).</summary>
    private static readonly string[] UserRoles = new[] { "cnas-user" };

    /// <summary>Expected names emitted by the region-scope filter (CA1861).</summary>
    private static readonly string[] ExpectedRegionScopedNames = new[] { "Alpha", "Gamma" };

    /// <summary>Returns a fresh InMemory DB context with a unique database name.</summary>
    /// <returns>The wired context.</returns>
    private static CnasDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-abac-search-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds a ClaimsPrincipal that carries the supplied role(s) + optional region claim.</summary>
    /// <param name="roles">Roles to project onto <c>ClaimTypes.Role</c>.</param>
    /// <param name="regionCode">Optional region-code claim value.</param>
    /// <returns>The constructed principal.</returns>
    private static SC.ClaimsPrincipal NewPrincipal(IEnumerable<string> roles, string? regionCode = null)
    {
        var claims = new List<SC.Claim>();
        foreach (var r in roles)
        {
            claims.Add(new SC.Claim(SC.ClaimTypes.Role, r));
        }
        if (regionCode is not null)
        {
            claims.Add(new SC.Claim("region_code", regionCode));
        }
        return new SC.ClaimsPrincipal(new SC.ClaimsIdentity(claims, authenticationType: "test"));
    }

    /// <summary>Inserts an <see cref="AbacRuleSet"/> with the supplied default effect (no rules).</summary>
    /// <param name="db">Target context.</param>
    /// <param name="policyName">Stable policy name.</param>
    /// <param name="defaultEffect">Default effect when no rule matches.</param>
    /// <param name="ruleExpression">Optional rule expression to seed.</param>
    /// <returns>The seeded rule set.</returns>
    private static async Task<AbacRuleSet> SeedRuleSetAsync(
        CnasDbContext db,
        string policyName,
        AbacEffect defaultEffect,
        string? ruleExpression = null)
    {
        var rs = new AbacRuleSet
        {
            PolicyName = policyName,
            DisplayName = policyName,
            DefaultEffect = defaultEffect,
            IsActive = true,
            RegisteredByUserId = 1L,
            CreatedAtUtc = ClockNow,
            CreatedBy = "test",
        };
        if (ruleExpression is not null)
        {
            rs.Rules.Add(new AbacRule
            {
                OrderIndex = 0,
                Effect = AbacEffect.Allow,
                ConditionExpression = ruleExpression,
                IsActive = true,
                CreatedAtUtc = ClockNow,
                CreatedBy = "test",
            });
        }
        db.AbacRuleSets.Add(rs);
        await db.SaveChangesAsync();
        return rs;
    }

    /// <summary>Seeds an active <see cref="Solicitant"/> with the supplied region code.</summary>
    /// <param name="db">Target context.</param>
    /// <param name="displayName">Display-name string.</param>
    /// <param name="regionCode">Region-code value (nullable).</param>
    /// <returns>The seeded entity.</returns>
    private static async Task<Solicitant> SeedSolicitantAsync(
        CnasDbContext db,
        string displayName,
        string? regionCode)
    {
        var s = new Solicitant
        {
            NationalId = "1003600012347" + displayName.Length,
            NationalIdHash = IdHashHelper.Hash(displayName),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = displayName,
            RegionCode = regionCode,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        };
        db.Solicitants.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // ─────────────────────── super-role bypass ───────────────────────

    /// <summary>
    /// The super-role (cnas-admin) bypasses every per-domain rule check — the
    /// filter returns the query unchanged so admins see every row.
    /// </summary>
    [Fact]
    public async Task ApplyRowLevelScope_SuperRole_ReturnsQueryUnchanged()
    {
        using var db = NewContext();
        await SeedSolicitantAsync(db, "Alpha", regionCode: "CHIS");
        await SeedSolicitantAsync(db, "Beta", regionCode: "BLT");

        var filter = new AbacSearchRowLevelFilter(db);
        var principal = NewPrincipal(AdminRoles);

        var scoped = filter.ApplyRowLevelScope(db.Solicitants.AsQueryable(), principal, "applicants");
        var rows = scoped.ToList();

        rows.Should().HaveCount(2);
    }

    // ─────────────────────── no-rule deny ───────────────────────

    /// <summary>
    /// A non-super-role caller with NO matching rule set falls back to the
    /// safe-by-default deny — the filter projects onto an empty result set.
    /// </summary>
    [Fact]
    public async Task ApplyRowLevelScope_NoRuleSet_NonSuperRole_ReturnsEmpty()
    {
        using var db = NewContext();
        await SeedSolicitantAsync(db, "Alpha", regionCode: "CHIS");

        var filter = new AbacSearchRowLevelFilter(db);
        var principal = NewPrincipal(UserRoles, regionCode: "CHIS");

        var scoped = filter.ApplyRowLevelScope(db.Solicitants.AsQueryable(), principal, "applicants");
        var rows = scoped.ToList();

        rows.Should().BeEmpty();
    }

    // ─────────────────────── default-effect allow ───────────────────────

    /// <summary>
    /// A rule set with <see cref="AbacEffect.Allow"/> as default and no
    /// condition rules grants access to every row — the filter returns the
    /// query unchanged.
    /// </summary>
    [Fact]
    public async Task ApplyRowLevelScope_RuleSetDefaultAllow_ReturnsAllRows()
    {
        using var db = NewContext();
        await SeedSolicitantAsync(db, "Alpha", regionCode: "CHIS");
        await SeedSolicitantAsync(db, "Beta", regionCode: "BLT");
        await SeedRuleSetAsync(db, "SEARCH.APPLICANTS", AbacEffect.Allow);

        var filter = new AbacSearchRowLevelFilter(db);
        var principal = NewPrincipal(UserRoles);

        var scoped = filter.ApplyRowLevelScope(db.Solicitants.AsQueryable(), principal, "applicants");
        var rows = scoped.ToList();

        rows.Should().HaveCount(2);
    }

    // ─────────────────────── region-scoped filter ───────────────────────

    /// <summary>
    /// A rule set whose condition expression references
    /// <c>subject.regionCode == resource.regionCode</c> filters the projected
    /// rows so only rows matching the caller's region code (or rows with a
    /// null region code, treated as "national") pass through.
    /// </summary>
    [Fact]
    public async Task ApplyRowLevelScope_RegionScopedRule_FiltersByCallerRegion()
    {
        using var db = NewContext();
        await SeedSolicitantAsync(db, "Alpha", regionCode: "CHIS");
        await SeedSolicitantAsync(db, "Beta", regionCode: "BLT");
        await SeedSolicitantAsync(db, "Gamma", regionCode: null); // "national"

        await SeedRuleSetAsync(
            db,
            "SEARCH.APPLICANTS",
            AbacEffect.Deny,
            ruleExpression: "subject.regionCode == resource.regionCode");

        var filter = new AbacSearchRowLevelFilter(db);
        var principal = NewPrincipal(UserRoles, regionCode: "CHIS");

        var scoped = filter.ApplyRowLevelScope(db.Solicitants.AsQueryable(), principal, "applicants");
        var rows = scoped.Select(s => s.DisplayName).ToList();

        // CHIS row passes the equality; the null-region "Gamma" passes per
        // the national-data semantics; "Beta" (BLT) is filtered out.
        rows.Should().BeEquivalentTo(ExpectedRegionScopedNames);
    }
}
