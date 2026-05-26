using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Registers;

/// <summary>
/// R1601 / TOR Annex 3.9 — read-only projection of the
/// <c>RegistrulDeciziilor</c> register over the existing <c>Document</c>
/// aggregate. Rows where <see cref="Document.Kind"/> equals
/// <see cref="DocumentKind.Decision"/> participate.
/// </summary>
/// <remarks>
/// Backed by <see cref="IReadOnlyCnasDbContext"/> so the listing routes to the
/// Postgres streaming replica per ARH 025.
/// </remarks>
public sealed class DecisionsRegister : IDecisionsRegister
{
    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private readonly IReadOnlyCnasDbContext _db;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the projection with its read-only context + Sqid encoder.</summary>
    /// <param name="db">Read-only EF Core context routed to the replica.</param>
    /// <param name="sqids">Sqid encoder.</param>
    public DecisionsRegister(IReadOnlyCnasDbContext db, ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        _db = db;
        _sqids = sqids;
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<DecisionRegisterRowDto>>> ListAsync(
        DecisionRegisterFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.FromUtc.HasValue && filter.ToUtc.HasValue && filter.FromUtc.Value > filter.ToUtc.Value)
        {
            return Result<PagedResult<DecisionRegisterRowDto>>.Failure(
                ErrorCodes.ValidationFailed,
                "FromUtc must be ≤ ToUtc.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var q = _db.Documents
            .Where(d => d.IsActive && d.Kind == DocumentKind.Decision);

        if (filter.FromUtc is { } from)
        {
            q = q.Where(d => d.CreatedAtUtc >= from);
        }
        if (filter.ToUtc is { } to)
        {
            q = q.Where(d => d.CreatedAtUtc < to);
        }
        if (!string.IsNullOrWhiteSpace(filter.DecisionTypeCode))
        {
            // The Document table doesn't carry an explicit type-code column today;
            // the Decision template code is embedded in `Title` (per the
            // RecoveryDecisionService / DecisionRecomputeService writers). EF Core
            // InMemory supports `Contains` against materialised strings, and the
            // Postgres provider lowers the same expression to ILIKE (`pg_trgm`
            // GIN index on `Title` already exists per the Annex-3.9 schema).
            var code = filter.DecisionTypeCode;
            q = q.Where(d => d.Title.Contains(code));
        }

        var total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await q
            .OrderByDescending(d => d.CreatedAtUtc)
            .ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.CreatedAtUtc,
                d.Verdict,
                d.VerdictNote,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(d => new DecisionRegisterRowDto(
                Sqid: _sqids.Encode(d.Id),
                DecisionNumber: BuildDecisionNumber(d.Id, d.CreatedAtUtc),
                DecisionTypeCode: InferTypeCode(d.Title),
                BeneficiaryIdnp: null,
                IssuedAtUtc: d.CreatedAtUtc,
                EffectiveFromDate: null,
                EffectiveToDate: null,
                Amount: TryReadAmount(d.VerdictNote),
                Status: InferStatus(d.Verdict)))
            .ToList();

        return Result<PagedResult<DecisionRegisterRowDto>>.Success(
            new PagedResult<DecisionRegisterRowDto>(items, page, pageSize, total));
    }

    /// <summary>Builds a deterministic public decision number from the row id + year.</summary>
    private static string BuildDecisionNumber(long id, DateTime issuedAtUtc) =>
        $"DEC-{issuedAtUtc:yyyy}-{id:000000}";

    /// <summary>
    /// Recovers the stable template code from the embedded prefix in
    /// <see cref="Document.Title"/>. Falls back to <c>DECIZIE</c> when no known
    /// prefix matches.
    /// </summary>
    private static string InferTypeCode(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "DECIZIE";
        // Known prefixes; order is significant — longer prefixes first.
        string[] candidates =
        [
            "DECIZIE_RECUPERARE_SUME",
            "DECIZIE_AJUSTARE_SUME",
            "decizie-recuperare-sume",
            "decizie-ajustare-sume",
            "DECIZIE_PENSIE",
            "DECIZIE_SUSPENDARE_PLATA",
            "DECIZIE",
        ];
        foreach (var c in candidates)
        {
            if (title.Contains(c, StringComparison.OrdinalIgnoreCase))
            {
                return c.ToUpperInvariant().Replace('-', '_');
            }
        }
        return "DECIZIE";
    }

    /// <summary>
    /// Best-effort extraction of the amount embedded in a recovery-decision /
    /// recompute envelope JSON on <see cref="Document.VerdictNote"/>. Returns
    /// <c>null</c> for non-recovery rows.
    /// </summary>
    private static decimal? TryReadAmount(string? verdictNote)
    {
        if (string.IsNullOrWhiteSpace(verdictNote))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(verdictNote);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (doc.RootElement.TryGetProperty("amount", out var amt)
                && amt.ValueKind == JsonValueKind.Number)
            {
                return amt.GetDecimal();
            }
            if (doc.RootElement.TryGetProperty("amount", out var amtStr)
                && amtStr.ValueKind == JsonValueKind.String
                && decimal.TryParse(amtStr.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Maps the <c>Document.Verdict</c> ordinal into a stable status name.</summary>
    private static string InferStatus(int? verdict) => verdict switch
    {
        null => "ISSUED",
        0 => "ISSUED",
        1 => "ACKNOWLEDGED",
        2 => "PARTIALLY_RECOVERED",
        3 => "FULLY_RECOVERED",
        _ => "OTHER",
    };
}
