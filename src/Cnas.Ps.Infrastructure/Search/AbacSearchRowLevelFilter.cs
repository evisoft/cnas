using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Search;

/// <summary>
/// R0526 / TOR CF 03.10 — production <see cref="ISearchRowLevelFilter"/>
/// implementation that consults the ABAC rule registry to scope a per-domain
/// search query at row level. Pure read; never mutates state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm.</b>
/// (1) If the caller carries any role in <see cref="SuperRoles"/>, return the
///     query unchanged.
/// (2) Look up the <see cref="AbacRuleSet"/> keyed by <c>SEARCH.{DOMAIN}</c>
///     (upper-snake-cased domain).
/// (3) If no rule set is found, collapse the query to empty (default-deny per
///     CLAUDE.md secure-by-default).
/// (4) If the rule set's <see cref="AbacRuleSet.DefaultEffect"/> is
///     <see cref="AbacEffect.Allow"/> and the set has no active rules, return
///     the query unchanged.
/// (5) Otherwise inspect the active rules' condition expressions for a
///     region-scope clause (<c>subject.regionCode == resource.regionCode</c>)
///     and apply a region filter on entities that carry a
///     <c>RegionCode</c> property — rows with a null region pass through
///     (the "national" semantic per
///     <see cref="Cnas.Ps.Core.Domain.Solicitant.RegionCode"/>).
/// </para>
/// <para>
/// <b>Why a pragmatic projection.</b> The full ABAC engine evaluates one
/// condition per row, which would defeat any database index. This filter
/// recognises the specific row-scope pattern used by the current rule set
/// (region match) and projects it onto a database-side
/// <see cref="Expression{TDelegate}"/> so the predicate runs server-side.
/// Future iterations may grow the recognised patterns; the safe default for
/// anything unrecognised is the rule set's default effect (Deny → empty).
/// </para>
/// </remarks>
public sealed class AbacSearchRowLevelFilter : ISearchRowLevelFilter
{
    /// <summary>
    /// Roles that bypass row-level scoping entirely. <c>cnas-admin</c> is the
    /// functional super-user; <c>cnas-tech-admin</c> is the operational
    /// super-user; <c>security-officer</c> is the security-domain super-user.
    /// </summary>
    public static readonly IReadOnlyList<string> SuperRoles = new[]
    {
        "cnas-admin",
        "cnas-tech-admin",
        "security-officer",
    };

    /// <summary>The token expression treated as the "match on region" canonical clause.</summary>
    private const string RegionMatchClause = "subject.regionCode == resource.regionCode";

    private readonly IReadOnlyCnasDbContext _db;

    /// <summary>Creates the filter with its read-only context dependency.</summary>
    /// <param name="db">Read-only DB context routed to the streaming replica.</param>
    public AbacSearchRowLevelFilter(IReadOnlyCnasDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public IQueryable<T> ApplyRowLevelScope<T>(
        IQueryable<T> query,
        ClaimsPrincipal user,
        string domain)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        // Super-role bypass — admins see everything.
        if (HasSuperRole(user))
        {
            return query;
        }

        var policyName = "SEARCH." + domain.ToUpperInvariant().Replace('-', '_');
        var ruleSet = _db.AbacRuleSets
            .AsNoTracking()
            .Where(rs => rs.IsActive && rs.PolicyName == policyName)
            .Select(rs => new
            {
                rs.DefaultEffect,
                Rules = rs.Rules
                    .Where(r => r.IsActive)
                    .Select(r => r.ConditionExpression)
                    .ToList(),
            })
            .FirstOrDefault();

        if (ruleSet is null)
        {
            // Default deny — collapse to empty.
            return query.Where(_ => false);
        }

        // Rule set found. Inspect its rules for the region-scope clause.
        var hasRegionClause = ruleSet.Rules.Any(expr =>
            !string.IsNullOrWhiteSpace(expr)
            && expr.Replace(" ", string.Empty)
                .Contains(RegionMatchClause.Replace(" ", string.Empty), StringComparison.Ordinal));

        if (hasRegionClause)
        {
            var regionCode = user.FindFirst("region_code")?.Value;
            if (string.IsNullOrWhiteSpace(regionCode))
            {
                // Caller has no region but the rule requires a region match —
                // only the "national" (null region) rows can possibly match.
                return ApplyRegionPredicate<T>(query, expectedRegion: null, includeNulls: true);
            }
            return ApplyRegionPredicate<T>(query, expectedRegion: regionCode, includeNulls: true);
        }

        // No row-scope predicate found. Fall back to the default effect.
        return ruleSet.DefaultEffect == AbacEffect.Allow
            ? query
            : query.Where(_ => false);
    }

    /// <summary>
    /// Returns <see langword="true"/> when any role claim on
    /// <paramref name="user"/> appears in <see cref="SuperRoles"/>.
    /// </summary>
    /// <param name="user">The calling principal.</param>
    /// <returns>True when the caller carries a configured super-role.</returns>
    private static bool HasSuperRole(ClaimsPrincipal user)
    {
        foreach (var super in SuperRoles)
        {
            if (user.IsInRole(super))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Applies a region-scope predicate on the supplied query. Entities that
    /// do not carry a <c>RegionCode</c> property are returned unchanged so
    /// the filter is a pass-through for unsupported types (the per-domain
    /// projector remains the authority on which entities carry a region).
    /// </summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="query">The pre-filtered query.</param>
    /// <param name="expectedRegion">The region code the caller is assigned to (nullable).</param>
    /// <param name="includeNulls">When true, rows with a null region pass through ("national" data).</param>
    /// <returns>The scoped query.</returns>
    private static IQueryable<T> ApplyRegionPredicate<T>(
        IQueryable<T> query,
        string? expectedRegion,
        bool includeNulls)
    {
        var regionProp = typeof(T).GetProperty("RegionCode",
            BindingFlags.Instance | BindingFlags.Public);
        if (regionProp is null || regionProp.PropertyType != typeof(string))
        {
            return query; // entity does not carry a region — caller decides at projector level
        }

        var param = Expression.Parameter(typeof(T), "e");
        Expression accessor = Expression.Property(param, regionProp);

        Expression predicate;
        if (expectedRegion is null)
        {
            // No expected region — only null-region rows pass.
            predicate = Expression.Equal(accessor, Expression.Constant(null, typeof(string)));
        }
        else
        {
            var matches = Expression.Equal(accessor, Expression.Constant(expectedRegion, typeof(string)));
            if (includeNulls)
            {
                var isNull = Expression.Equal(accessor, Expression.Constant(null, typeof(string)));
                predicate = Expression.OrElse(matches, isNull);
            }
            else
            {
                predicate = matches;
            }
        }

        var lambda = Expression.Lambda<Func<T, bool>>(predicate, param);
        return query.Where(lambda);
    }
}
