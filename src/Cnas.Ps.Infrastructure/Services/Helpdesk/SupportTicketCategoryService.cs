using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — production implementation of
/// <see cref="ISupportTicketCategoryService"/>. CRUD over the
/// <c>SupportTicketCategory</c> registry; every mutation emits a
/// Critical-severity audit row.
/// </summary>
public sealed class SupportTicketCategoryService : ISupportTicketCategoryService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<SupportTicketCategoryCreateInputDto> _createValidator;
    private readonly IValidator<SupportTicketCategoryModifyInputDto> _modifyValidator;
    private readonly IValidator<SupportTicketCategoryFilterDto> _filterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="createValidator">Validator for create input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="filterValidator">Validator for list-filter input.</param>
    public SupportTicketCategoryService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<SupportTicketCategoryCreateInputDto> createValidator,
        IValidator<SupportTicketCategoryModifyInputDto> modifyValidator,
        IValidator<SupportTicketCategoryFilterDto> filterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _createValidator = createValidator;
        _modifyValidator = modifyValidator;
        _filterValidator = filterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> CreateAsync(
        SupportTicketCategoryCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _createValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketCategoryDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var duplicate = await _db.SupportTicketCategories
            .AnyAsync(c => c.Code == input.Code, cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<SupportTicketCategoryDto>.Failure(
                ISupportTicketCategoryService.DuplicateCategoryCodeCode,
                $"A helpdesk category with Code '{input.Code}' already exists.");
        }

        var severity = Enum.Parse<SupportTicketSeverity>(input.DefaultSeverity, ignoreCase: false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";

        var category = new SupportTicketCategory
        {
            Code = input.Code,
            DisplayName = input.DisplayName,
            Description = input.Description,
            DefaultSeverity = severity,
            FirstResponseSlaMinutes = input.FirstResponseSlaMinutes,
            ResolutionSlaMinutes = input.ResolutionSlaMinutes,
            EscalationQueueCode = input.EscalationQueueCode,
            RegisteredByUserId = _caller.UserId ?? 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.SupportTicketCategories.Add(category);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISupportTicketCategoryService.AuditCategoryCreated,
            AuditSeverity.Critical,
            actor,
            category.Id,
            new
            {
                categorySqid = _sqids.Encode(category.Id),
                category.Code,
                category.FirstResponseSlaMinutes,
                category.ResolutionSlaMinutes,
                defaultSeverity = category.DefaultSeverity.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SupportTicketCategoryDto>.Success(ToDto(category));
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> ModifyAsync(
        string categorySqid,
        SupportTicketCategoryModifyInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = await _modifyValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketCategoryDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var loaded = await LoadAsync(categorySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketCategoryDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var category = loaded.Value;

        if (input.DisplayName is not null) category.DisplayName = input.DisplayName;
        if (input.Description is not null) category.Description = input.Description;
        if (input.DefaultSeverity is not null)
        {
            category.DefaultSeverity = Enum.Parse<SupportTicketSeverity>(input.DefaultSeverity, ignoreCase: false);
        }
        if (input.FirstResponseSlaMinutes is not null) category.FirstResponseSlaMinutes = input.FirstResponseSlaMinutes.Value;
        if (input.ResolutionSlaMinutes is not null) category.ResolutionSlaMinutes = input.ResolutionSlaMinutes.Value;
        if (input.EscalationQueueCode is not null) category.EscalationQueueCode = input.EscalationQueueCode;

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        category.UpdatedAtUtc = now;
        category.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISupportTicketCategoryService.AuditCategoryModified,
            AuditSeverity.Critical,
            actor,
            category.Id,
            new
            {
                categorySqid = _sqids.Encode(category.Id),
                category.Code,
                changeReason = input.ChangeReason,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SupportTicketCategoryDto>.Success(ToDto(category));
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> ActivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(categorySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketCategoryDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var category = loaded.Value;
        if (category.IsActive)
        {
            return Result<SupportTicketCategoryDto>.Failure(
                ISupportTicketCategoryService.InvalidTransitionCode,
                "Category is already Active.");
        }
        return await ApplyTransitionAsync(category, newIsActive: true, transitionLabel: "Activate", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> DeactivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(categorySqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SupportTicketCategoryDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var category = loaded.Value;
        if (!category.IsActive)
        {
            return Result<SupportTicketCategoryDto>.Failure(
                ISupportTicketCategoryService.InvalidTransitionCode,
                "Category is already Inactive.");
        }
        return await ApplyTransitionAsync(category, newIsActive: false, transitionLabel: "Deactivate", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> GetByIdAsync(
        string categorySqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(categorySqid);
        if (decoded.IsFailure)
        {
            return Result<SupportTicketCategoryDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.SupportTicketCategories
            .FirstOrDefaultAsync(c => c.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SupportTicketCategoryDto>.Failure(ErrorCodes.NotFound, "Helpdesk category not found.")
            : Result<SupportTicketCategoryDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryDto>> GetByCodeAsync(
        string categoryCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
        {
            return Result<SupportTicketCategoryDto>.Failure(ErrorCodes.ValidationFailed, "Code is required.");
        }
        var row = await _read.SupportTicketCategories
            .FirstOrDefaultAsync(c => c.Code == categoryCode, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SupportTicketCategoryDto>.Failure(ErrorCodes.NotFound, "Helpdesk category not found.")
            : Result<SupportTicketCategoryDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<SupportTicketCategoryPageDto>> ListAsync(
        SupportTicketCategoryFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<SupportTicketCategoryPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<SupportTicketCategory> q = _read.SupportTicketCategories;
        if (filter.IsActive is not null)
        {
            var wantActive = filter.IsActive.Value;
            q = q.Where(c => c.IsActive == wantActive);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderBy(c => c.Code)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new SupportTicketCategoryPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<SupportTicketCategoryPageDto>.Success(page);
    }

    /// <summary>Loads a category by Sqid, returning a friendly failure on missing.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded entity on success.</returns>
    private async Task<Result<SupportTicketCategory>> LoadAsync(string categorySqid, CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(categorySqid);
        if (decoded.IsFailure)
        {
            return Result<SupportTicketCategory>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.SupportTicketCategories
            .FirstOrDefaultAsync(c => c.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<SupportTicketCategory>.Failure(ErrorCodes.NotFound, "Helpdesk category not found.")
            : Result<SupportTicketCategory>.Success(row);
    }

    /// <summary>Flips the IsActive flag + emits the transition audit row.</summary>
    /// <param name="category">Loaded category.</param>
    /// <param name="newIsActive">Target active flag.</param>
    /// <param name="transitionLabel">Audit label describing the transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<SupportTicketCategoryDto>> ApplyTransitionAsync(
        SupportTicketCategory category,
        bool newIsActive,
        string transitionLabel,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        category.IsActive = newIsActive;
        category.UpdatedAtUtc = now;
        category.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            ISupportTicketCategoryService.AuditCategoryTransitioned,
            AuditSeverity.Critical,
            actor,
            category.Id,
            new
            {
                categorySqid = _sqids.Encode(category.Id),
                category.Code,
                transition = transitionLabel,
                isActive = category.IsActive,
                atUtc = now.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<SupportTicketCategoryDto>.Success(ToDto(category));
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(SupportTicketCategory),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="c">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private SupportTicketCategoryDto ToDto(SupportTicketCategory c) => new(
        Id: _sqids.Encode(c.Id),
        Code: c.Code,
        DisplayName: c.DisplayName,
        Description: c.Description,
        DefaultSeverity: c.DefaultSeverity.ToString(),
        FirstResponseSlaMinutes: c.FirstResponseSlaMinutes,
        ResolutionSlaMinutes: c.ResolutionSlaMinutes,
        EscalationQueueCode: c.EscalationQueueCode,
        IsActive: c.IsActive);
}
