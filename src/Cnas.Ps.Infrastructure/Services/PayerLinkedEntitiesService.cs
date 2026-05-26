using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Payers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0301 / R0803 / ARH 028 — concrete implementation of
/// <see cref="IPayerLinkedEntitiesService"/> owning supersession-based mutations on
/// the Payer (Plătitor) child tables (address, contact, CAEM activities, history,
/// and the R0803 bank-account / secondary-contact extensions).
/// </summary>
/// <param name="db">EF Core DB context.</param>
/// <param name="clock">UTC clock — never use <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="sqids">Sqid encoder for DTO id round-tripping.</param>
/// <param name="caller">Authenticated caller information.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="hasher">Deterministic hasher backing the encrypted IBAN shadow column.</param>
public sealed class PayerLinkedEntitiesService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    IAuditService audit,
    IDeterministicHasher hasher) : IPayerLinkedEntitiesService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IDeterministicHasher _hasher = hasher;

    private const string EvtAddressUpdated = "PAYERADDRESS.UPDATED";
    private const string EvtContactUpdated = "PAYERCONTACT.UPDATED";
    private const string EvtActivityAdded = "PAYERACTIVITY.ADDED";
    private const string EvtActivityEnded = "PAYERACTIVITY.ENDED";
    private const string EvtBankAccountAdded = "PAYERBANKACCOUNT.ADDED";
    private const string EvtBankAccountClosed = "PAYERBANKACCOUNT.CLOSED";
    private const string EvtSecondaryContactAdded = "PAYERSECONDARYCONTACT.ADDED";
    private const string EvtSecondaryContactClosed = "PAYERSECONDARYCONTACT.CLOSED";

    /// <inheritdoc />
    public async Task<Result<PayerAddressDto>> UpdateAddressAsync(
        long payerId,
        PayerAddressInputDto input,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var current = await _db.PayerAddresses
            .Where(a => a.PayerId == payerId && a.ValidToUtc == null)
            .OrderByDescending(a => a.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        // R0301 — no-op short-circuit when nothing changed.
        if (current is not null && AddressEquals(current, input))
        {
            return Result<PayerAddressDto>.Success(ToAddressDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;

            // Per-field diff rows on PayerHistory so the operator detail screen can
            // show "what changed and when" without scanning the AuditLog.
            await WriteFieldDiffsAsync(payerId, "Street", current.Street, input.Street, changeReason, now, ct)
                .ConfigureAwait(false);
            await WriteFieldDiffsAsync(payerId, "City", current.City, input.City, changeReason, now, ct)
                .ConfigureAwait(false);
            await WriteFieldDiffsAsync(payerId, "Region", current.Region, input.Region, changeReason, now, ct)
                .ConfigureAwait(false);
            await WriteFieldDiffsAsync(payerId, "PostalCode", current.PostalCode, input.PostalCode, changeReason, now, ct)
                .ConfigureAwait(false);
            await WriteFieldDiffsAsync(payerId, "Country", current.Country, input.Country, changeReason, now, ct)
                .ConfigureAwait(false);
        }

        var inserted = new PayerAddress
        {
            PayerId = payerId,
            Street = input.Street,
            City = input.City,
            Region = input.Region,
            PostalCode = input.PostalCode,
            Country = string.IsNullOrEmpty(input.Country) ? "MD" : input.Country,
            ValidFromUtc = now,
            ValidToUtc = null,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PayerAddresses.Add(inserted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitSupersedeAuditAsync(
            EvtAddressUpdated, payerId, AddressHash(current), AddressHash(inserted), changeReason, ct)
            .ConfigureAwait(false);

        return Result<PayerAddressDto>.Success(ToAddressDto(inserted));
    }

    /// <inheritdoc />
    public async Task<Result<PayerContactDto>> UpdateContactAsync(
        long payerId,
        PayerContactInputDto input,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var current = await _db.PayerContacts
            .Where(c => c.PayerId == payerId && c.ValidToUtc == null)
            .OrderByDescending(c => c.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current is not null && ContactEquals(current, input))
        {
            return Result<PayerContactDto>.Success(ToContactDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var inserted = new PayerContact
        {
            PayerId = payerId,
            PhoneE164 = input.PhoneE164,
            Email = input.Email,
            ContactPersonName = input.ContactPersonName,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PayerContacts.Add(inserted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0182 — Sensitive audit; field values are hashed (no PII in details).
        await EmitSupersedeAuditAsync(
            EvtContactUpdated, payerId, ContactHash(current), ContactHash(inserted), changeReason, ct,
            AuditSeverity.Sensitive).ConfigureAwait(false);

        return Result<PayerContactDto>.Success(ToContactDto(inserted));
    }

    /// <inheritdoc />
    public async Task<Result<PayerActivityCaemDto>> AddActivityCaemAsync(
        long payerId,
        PayerActivityCaemInputDto input,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _clock.UtcNow;
        var inserted = new PayerActivityCAEM
        {
            PayerId = payerId,
            CaemCode = input.CaemCode,
            CaemDescription = input.CaemDescription,
            IsPrimary = input.IsPrimary,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PayerActivities.Add(inserted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitSupersedeAuditAsync(
            EvtActivityAdded, payerId, null, $"caem:{input.CaemCode}", changeReason, ct)
            .ConfigureAwait(false);

        return Result<PayerActivityCaemDto>.Success(ToActivityDto(inserted));
    }

    /// <inheritdoc />
    public async Task<Result> EndActivityCaemAsync(
        long activityId,
        string? changeReason,
        CancellationToken ct = default)
    {
        var row = await _db.PayerActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.ValidToUtc == null, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Activity not found or already ended.");
        }
        var now = _clock.UtcNow;
        row.ValidToUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        if (!string.IsNullOrWhiteSpace(changeReason))
        {
            row.ChangeReason = changeReason;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitSupersedeAuditAsync(
            EvtActivityEnded, row.PayerId, $"caem:{row.CaemCode}", null, changeReason, ct)
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PayerAddressDto>>> ListAddressHistoryAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerAddresses
            .Where(a => a.PayerId == payerId)
            .OrderByDescending(a => a.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PayerAddressDto>>.Success(rows.Select(ToAddressDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PayerContactDto>>> ListContactHistoryAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerContacts
            .Where(c => c.PayerId == payerId)
            .OrderByDescending(c => c.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PayerContactDto>>.Success(rows.Select(ToContactDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PayerActivityCaemDto>>> ListActivityHistoryAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerActivities
            .Where(a => a.PayerId == payerId)
            .OrderByDescending(a => a.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PayerActivityCaemDto>>.Success(rows.Select(ToActivityDto).ToList());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PayerHistoryDto>> ListHistoryAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerHistory
            .Where(h => h.PayerId == payerId)
            .OrderByDescending(h => h.ChangedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(h => new PayerHistoryDto(
            _sqids.Encode(h.Id), _sqids.Encode(h.PayerId), h.FieldName,
            h.OldValue, h.NewValue, h.ChangeReason, h.ChangedAtUtc, h.RecordedByUserSqid))
            .ToList();
    }

    // ───────────────── helpers ─────────────────

    /// <summary>Returns true when the supplied address row matches the input field-for-field.</summary>
    private static bool AddressEquals(PayerAddress row, PayerAddressInputDto input) =>
        string.Equals(row.Street, input.Street, StringComparison.Ordinal)
        && string.Equals(row.City, input.City, StringComparison.Ordinal)
        && string.Equals(row.Region, input.Region, StringComparison.Ordinal)
        && string.Equals(row.PostalCode, input.PostalCode, StringComparison.Ordinal)
        && string.Equals(row.Country, input.Country, StringComparison.Ordinal);

    /// <summary>Returns true when the supplied contact row matches the input field-for-field.</summary>
    private static bool ContactEquals(PayerContact row, PayerContactInputDto input) =>
        string.Equals(row.PhoneE164, input.PhoneE164, StringComparison.Ordinal)
        && string.Equals(row.Email, input.Email, StringComparison.Ordinal)
        && string.Equals(row.ContactPersonName, input.ContactPersonName, StringComparison.Ordinal);

    /// <summary>Writes one <see cref="PayerHistory"/> row per changed field.</summary>
    private async Task WriteFieldDiffsAsync(
        long payerId, string field, string? oldValue, string? newValue,
        string? changeReason, DateTime now, CancellationToken ct)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return;
        }
        _db.PayerHistory.Add(new PayerHistory
        {
            PayerId = payerId,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            ChangeReason = changeReason,
            ChangedAtUtc = now,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        });
        // Persist in the same SaveChangesAsync as the new row insert above — caller will
        // invoke SaveChangesAsync once after the diff loop completes.
        await Task.CompletedTask;
        _ = ct;
    }

    /// <summary>Builds the canonical hash representation of an address row (for audit details).</summary>
    private static string? AddressHash(PayerAddress? row)
    {
        if (row is null) return null;
        var canonical = string.Join('|', row.Street, row.City, row.Region, row.PostalCode, row.Country);
        return Sha256Hex(canonical);
    }

    /// <summary>Builds the canonical hash of a contact row (no PII leaked).</summary>
    private static string? ContactHash(PayerContact? row)
    {
        if (row is null) return null;
        var canonical = string.Join('|', row.PhoneE164 ?? "", row.Email ?? "", row.ContactPersonName ?? "");
        return Sha256Hex(canonical);
    }

    /// <summary>SHA-256 hex of the supplied canonical string (used to hide PII in audit details).</summary>
    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private async Task EmitSupersedeAuditAsync(
        string eventCode, long payerId, string? fromHash, string? toHash,
        string? changeReason, CancellationToken ct,
        AuditSeverity severity = AuditSeverity.Notice)
    {
        var detail = JsonSerializer.Serialize(new
        {
            parentSqid = _sqids.Encode(payerId),
            fromValuesHash = fromHash,
            toValuesHash = toHash,
            changeReason,
        });
        await _audit.RecordAsync(
            eventCode, severity, _caller.UserSqid ?? "system",
            "PayerLinkedEntity", payerId, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    private PayerAddressDto ToAddressDto(PayerAddress r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.PayerId),
        r.Street, r.City, r.Region, r.PostalCode, r.Country,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private PayerContactDto ToContactDto(PayerContact r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.PayerId),
        r.PhoneE164, r.Email, r.ContactPersonName,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private PayerActivityCaemDto ToActivityDto(PayerActivityCAEM r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.PayerId),
        r.CaemCode, r.CaemDescription, r.IsPrimary,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    // ───────────────── R0803 — bank accounts ─────────────────

    /// <inheritdoc />
    public async Task<Result<PayerBankAccountDto>> AddBankAccountAsync(
        long payerId,
        PayerBankAccountInputDto input,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Validate IBAN/BIC/Currency shape at the service boundary. The validator
        // is the single source of truth; running it here avoids relying on a
        // controller-level pipeline being wired and prevents bad data from reaching
        // the DB via direct service callers (e.g. background jobs, tests).
        var validator = new Cnas.Ps.Application.Validators.PayerBankAccountInputDtoValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<PayerBankAccountDto>.Failure(
                ErrorCodes.InvalidIban,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var canonicalIban = Cnas.Ps.Application.Validators.PayerBankAccountInputDtoValidator
            .CanonicaliseIban(input.Iban);
        var ibanHash = _hasher.ComputeHash(canonicalIban);

        // Duplicate-IBAN guard — the filtered unique index on Postgres backs this
        // invariant too, but we check here so the InMemory test provider produces
        // a clean validation failure instead of a uniqueness-violation exception.
        var dup = await _db.PayerBankAccounts
            .AnyAsync(b => b.PayerId == payerId && b.ValidToUtc == null && b.IbanHash == ibanHash, ct)
            .ConfigureAwait(false);
        if (dup)
        {
            return Result<PayerBankAccountDto>.Failure(
                ErrorCodes.InvalidIban, "IBAN already on file for this payer.");
        }

        var now = _clock.UtcNow;
        if (input.IsPrimary)
        {
            // Supersede the current primary, if any.
            var currentPrimary = await _db.PayerBankAccounts
                .Where(b => b.PayerId == payerId && b.ValidToUtc == null && b.IsPrimary)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in currentPrimary)
            {
                row.ValidToUtc = now;
                row.UpdatedAtUtc = now;
                row.UpdatedBy = _caller.UserSqid;
            }
        }

        var inserted = new PayerBankAccount
        {
            PayerId = payerId,
            AccountHolderName = input.AccountHolderName,
            Iban = canonicalIban,
            IbanHash = ibanHash,
            BankName = input.BankName,
            BankBic = input.BankBic,
            IsPrimary = input.IsPrimary,
            Currency = string.IsNullOrEmpty(input.Currency) ? "MDL" : input.Currency,
            ValidFromUtc = now,
            ValidToUtc = null,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PayerBankAccounts.Add(inserted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitBankAccountAuditAsync(
            EvtBankAccountAdded, payerId, ibanHash, changeReason, ct).ConfigureAwait(false);

        return Result<PayerBankAccountDto>.Success(ToBankAccountDto(inserted));
    }

    /// <inheritdoc />
    public async Task<Result> CloseBankAccountAsync(
        long bankAccountId,
        string? changeReason,
        CancellationToken ct = default)
    {
        var row = await _db.PayerBankAccounts
            .FirstOrDefaultAsync(b => b.Id == bankAccountId && b.ValidToUtc == null, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Bank account not found or already closed.");
        }
        var now = _clock.UtcNow;
        row.ValidToUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        if (!string.IsNullOrWhiteSpace(changeReason))
        {
            row.ChangeReason = changeReason;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitBankAccountAuditAsync(
            EvtBankAccountClosed, row.PayerId, row.IbanHash, changeReason, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PayerBankAccountDto>>> ListCurrentBankAccountsAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerBankAccounts
            .Where(b => b.PayerId == payerId && b.ValidToUtc == null)
            .OrderByDescending(b => b.IsPrimary).ThenBy(b => b.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PayerBankAccountDto>>.Success(
            rows.Select(ToBankAccountDto).ToList());
    }

    // ───────────────── R0803 — secondary contacts ─────────────────

    /// <inheritdoc />
    public async Task<Result<PayerSecondaryContactDto>> AddSecondaryContactAsync(
        long payerId,
        PayerSecondaryContactInputDto input,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validator = new Cnas.Ps.Application.Validators.PayerSecondaryContactInputDtoValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<PayerSecondaryContactDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        var now = _clock.UtcNow;
        var row = new PayerSecondaryContact
        {
            PayerId = payerId,
            ContactPersonName = input.ContactPersonName,
            Role = input.Role,
            PhoneE164 = input.PhoneE164,
            Email = input.Email,
            ValidFromUtc = now,
            ValidToUtc = null,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PayerSecondaryContacts.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitSecondaryContactAuditAsync(
            EvtSecondaryContactAdded, payerId, row, changeReason, ct).ConfigureAwait(false);

        return Result<PayerSecondaryContactDto>.Success(ToSecondaryContactDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> CloseSecondaryContactAsync(
        long secondaryContactId,
        string? changeReason,
        CancellationToken ct = default)
    {
        var row = await _db.PayerSecondaryContacts
            .FirstOrDefaultAsync(s => s.Id == secondaryContactId && s.ValidToUtc == null, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Secondary contact not found or already closed.");
        }
        var now = _clock.UtcNow;
        row.ValidToUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        if (!string.IsNullOrWhiteSpace(changeReason))
        {
            row.ChangeReason = changeReason;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitSecondaryContactAuditAsync(
            EvtSecondaryContactClosed, row.PayerId, row, changeReason, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<PayerSecondaryContactDto>>> ListCurrentSecondaryContactsAsync(
        long payerId, CancellationToken ct = default)
    {
        var rows = await _db.PayerSecondaryContacts
            .Where(s => s.PayerId == payerId && s.ValidToUtc == null)
            .OrderBy(s => s.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PayerSecondaryContactDto>>.Success(
            rows.Select(ToSecondaryContactDto).ToList());
    }

    // ───────────────── R0803 — helpers ─────────────────

    /// <summary>Projects a <see cref="PayerBankAccount"/> entity to its DTO.</summary>
    private PayerBankAccountDto ToBankAccountDto(PayerBankAccount r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.PayerId),
        r.AccountHolderName, r.Iban, r.BankName, r.BankBic, r.IsPrimary, r.Currency,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    /// <summary>Projects a <see cref="PayerSecondaryContact"/> entity to its DTO.</summary>
    private PayerSecondaryContactDto ToSecondaryContactDto(PayerSecondaryContact r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.PayerId),
        r.ContactPersonName, r.Role, r.PhoneE164, r.Email,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    /// <summary>
    /// Emits a Sensitive audit row for a bank-account mutation. Carries only the
    /// first 8 chars of the IBAN hash (base64-safe slice) — never the plaintext IBAN.
    /// </summary>
    private async Task EmitBankAccountAuditAsync(
        string eventCode, long payerId, string ibanHash, string? changeReason, CancellationToken ct)
    {
        var prefix = string.IsNullOrEmpty(ibanHash)
            ? null
            : ibanHash.Length >= 8 ? ibanHash[..8] : ibanHash;
        var detail = JsonSerializer.Serialize(new
        {
            parentSqid = _sqids.Encode(payerId),
            ibanHashPrefix = prefix,
            changeReason,
        });
        await _audit.RecordAsync(
            eventCode, AuditSeverity.Sensitive, _caller.UserSqid ?? "system",
            "PayerBankAccount", payerId, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    /// <summary>Emits a Sensitive audit row for a secondary-contact mutation (no raw PII in details).</summary>
    private async Task EmitSecondaryContactAuditAsync(
        string eventCode, long payerId, PayerSecondaryContact row,
        string? changeReason, CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new
        {
            parentSqid = _sqids.Encode(payerId),
            role = row.Role,
            // Hash the contact-person name + phone + email so the audit trail can
            // confirm "this was the same contact" without exposing PII.
            contactHash = SecondaryContactHash(row),
            changeReason,
        });
        await _audit.RecordAsync(
            eventCode, AuditSeverity.Sensitive, _caller.UserSqid ?? "system",
            "PayerSecondaryContact", payerId, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    /// <summary>Canonical hash of a secondary-contact row for audit details (no PII leaked).</summary>
    private static string SecondaryContactHash(PayerSecondaryContact r)
    {
        var canonical = string.Join('|', r.ContactPersonName, r.PhoneE164 ?? "", r.Email ?? "");
        return Sha256Hex(canonical);
    }

    // Suppress unused-import warnings for the harness builder.
    private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;
}
