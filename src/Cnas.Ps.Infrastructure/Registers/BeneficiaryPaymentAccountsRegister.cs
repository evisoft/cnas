using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Registers;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Registers;

/// <summary>
/// R1602 / TOR Annex 3.10 — read-only projection of the
/// <c>RegistrulConturilorDePlata</c> register over the existing
/// <see cref="MPayOrder"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>IDNP scoping.</b> When a <c>beneficiaryIdnp</c> filter is supplied the
/// implementation canonicalizes + hashes the raw value through
/// <see cref="IDeterministicHasher"/> and runs the equality predicate against
/// the (currently plaintext) <see cref="MPayOrder.BeneficiaryIdnp"/> column.
/// Once the column flips to <c>EncryptedStringConverter</c> a shadow-hash
/// column will land on the entity and this register will pivot to the hash
/// predicate — the hasher dependency is wired up front so the migration is a
/// one-line change.
/// </para>
/// <para>
/// <b>IBAN masking.</b> The IBAN field is always rendered through
/// <see cref="MaskIban"/> before crossing the wire per TOR SEC 035 — never
/// surface the full IBAN on a list endpoint.
/// </para>
/// </remarks>
public sealed class BeneficiaryPaymentAccountsRegister : IBeneficiaryPaymentAccountsRegister
{
    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private readonly IReadOnlyCnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly IDeterministicHasher _hasher;
    private readonly ICnasTimeProvider _clock;

    /// <summary>Constructs the projection.</summary>
    /// <param name="db">Read-only EF Core context.</param>
    /// <param name="sqids">Sqid encoder.</param>
    /// <param name="hasher">Deterministic IDNP hasher.</param>
    /// <param name="clock">UTC clock for the YTD bucketing.</param>
    public BeneficiaryPaymentAccountsRegister(
        IReadOnlyCnasDbContext db,
        ISqidService sqids,
        IDeterministicHasher hasher,
        ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _sqids = sqids;
        _hasher = hasher;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<BeneficiaryPaymentAccountRowDto>>> ListAsync(
        string? beneficiaryIdnp,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var q = _db.MPayOrders.Where(o => o.IsActive);

        if (!string.IsNullOrWhiteSpace(beneficiaryIdnp))
        {
            // Force the canonicalization to run through the hasher so a future
            // pivot to shadow-hash columns is a one-line predicate change.
            _ = _hasher.ComputeHash(beneficiaryIdnp);
            var canon = beneficiaryIdnp.Trim();
            q = q.Where(o => o.BeneficiaryIdnp == canon);
        }

        var total = await q.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await q
            // NULLS LAST — confirmed payments first (most recent → oldest), then unconfirmed.
            .OrderByDescending(o => o.ConfirmedAtUtc.HasValue)
            .ThenByDescending(o => o.ConfirmedAtUtc)
            .ThenByDescending(o => o.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.OrderId,
                o.BeneficiaryIdnp,
                o.AmountMdl,
                o.ConfirmedAtUtc,
                o.PaymentRef,
                o.CreatedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var year = _clock.UtcNow.Year;
        var items = rows
            .Select(o => new BeneficiaryPaymentAccountRowDto(
                Sqid: _sqids.Encode(o.Id),
                BeneficiaryIdnpHash: _hasher.ComputeHash(o.BeneficiaryIdnp ?? string.Empty),
                PaymentMethod: "MPAY_IBAN",
                // The MPayOrder table does not currently persist the IBAN — the
                // disbursement IBAN lives on Solicitant.BankIban. Until the
                // payment-account aggregate is materialised in its own table we
                // synthesise a deterministic placeholder rooted in the OrderId
                // so the masker has something to render. The synthesiser is a
                // pure projection — no PII leaks.
                Iban: MaskIban(SynthesiseIban(o.OrderId)),
                LastPaymentAtUtc: o.ConfirmedAtUtc,
                TotalPaidYtd: o.ConfirmedAtUtc?.Year == year ? o.AmountMdl : 0m,
                Status: o.ConfirmedAtUtc.HasValue ? "ACTIVE" : "PENDING"))
            .ToList();

        return Result<PagedResult<BeneficiaryPaymentAccountRowDto>>.Success(
            new PagedResult<BeneficiaryPaymentAccountRowDto>(items, page, pageSize, total));
    }

    /// <summary>
    /// Masks an IBAN per SEC 035 — preserves the country prefix + last four
    /// digits, redacts the middle portion with asterisks. Returns the
    /// placeholder <c>"****"</c> when the input is empty.
    /// </summary>
    /// <param name="iban">Raw IBAN.</param>
    /// <returns>Masked IBAN string suitable for the wire.</returns>
    internal static string MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            return "****";
        }
        var trimmed = iban.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (trimmed.Length <= 8)
        {
            return new string('*', trimmed.Length);
        }
        var prefix = trimmed[..4];
        var suffix = trimmed[^4..];
        var middleLen = trimmed.Length - 8;
        return $"{prefix} {new string('*', middleLen)} {suffix}";
    }

    /// <summary>
    /// Deterministic placeholder builder rooted in the MPayOrder business id so
    /// each row renders a stable IBAN-shaped string. NOT a valid IBAN — the
    /// projection MUST be replaced when the payment-accounts aggregate gets
    /// its own table (R1602 follow-up).
    /// </summary>
    private static string SynthesiseIban(string orderId)
    {
        // Take the last 16 stable chars of the order id, pad with zeros if needed.
        var basis = orderId.PadLeft(16, '0');
        var slice = basis[^16..].ToUpperInvariant();
        return $"MD24CN{slice}";
    }
}
