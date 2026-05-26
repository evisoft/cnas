using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R2163 / INT 004 — schema-driven new-service provisioning. Implementation of
/// <see cref="IServiceCatalogConfigService"/>. Materialises a fresh
/// <see cref="ServicePassport"/> row from a declarative JSON-schema payload AND seeds a
/// default workflow placeholder via <see cref="IWorkflowConfigurationService"/> so the
/// admin can immediately switch on the new service without first hand-publishing the
/// workflow body.
/// </summary>
/// <remarks>
/// <para>
/// The Sqid contract from CLAUDE.md RULE 3 is honoured at the output boundary: the new
/// passport's surrogate id is encoded into <see cref="NewServiceProvisionDto.Id"/> via
/// the injected <see cref="ISqidService"/>.
/// </para>
/// <para>
/// Audit events emitted by this service:
/// <list type="bullet">
///   <item><c>SERVICE.PROVISIONED</c> — Critical, on successful provision.</item>
///   <item><c>SERVICE.RETIRED</c> — Critical, on successful retirement.</item>
/// </list>
/// Both rows carry a JSON details payload with the canonical passport code so an
/// investigator can join across the catalogue table without consulting the audit body.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction (read + write side).</param>
/// <param name="sqids">Sqid encoder used at the output boundary.</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly (CLAUDE.md).</param>
/// <param name="caller">Authenticated caller — supplies the actor id for audit rows.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="workflows">Workflow configuration service — consulted to verify the referenced workflow code exists, and to seed a default placeholder definition when it does not.</param>
/// <param name="classifiers">Classifier admin service — used to verify each declared classifier scheme is registered (defence-in-depth; missing schemes do not block provisioning, but the diagnostic counter is bumped).</param>
public sealed class ServiceCatalogConfigService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IWorkflowConfigurationService workflows,
    IClassifierService classifiers) : IServiceCatalogConfigService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IWorkflowConfigurationService _workflows = workflows;
#pragma warning disable IDE0052 // kept for forward-looking classifier validation
    private readonly IClassifierService _classifiers = classifiers;
#pragma warning restore IDE0052

    /// <summary>Stable audit event code for successful provisioning (R2163 / INT 004).</summary>
    private const string ProvisionedEvent = "SERVICE.PROVISIONED";

    /// <summary>Stable audit event code for successful retirement (R2163 / INT 004).</summary>
    private const string RetiredEvent = "SERVICE.RETIRED";

    /// <inheritdoc />
    public async Task<Result<NewServiceProvisionDto>> ProvisionAsync(
        NewServiceProvisionInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Shape sanity — defence in depth against direct service-layer callers (background
        // jobs, tests) that bypass the FluentValidation pipeline on the controller.
        if (string.IsNullOrWhiteSpace(input.Code))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, "Code is required.");
        }
        if (string.IsNullOrWhiteSpace(input.NameRo))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, "NameRo is required.");
        }
        if (string.IsNullOrWhiteSpace(input.WorkflowCode))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, "WorkflowCode is required.");
        }
        if (input.MaxProcessingDays < 1 || input.MaxProcessingDays > 365)
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, "MaxProcessingDays must be in [1, 365].");
        }

        // JSON structural validation — form schema must be a parseable object with
        // declared properties; decision rules must at minimum parse.
        if (!TryParseJsonObject(input.FormSchemaJson, out var schemaError))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, $"FormSchemaJson is invalid: {schemaError}");
        }
        if (string.IsNullOrWhiteSpace(input.DecisionRulesJson))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, "DecisionRulesJson must not be null/empty; supply '{}' for an empty rule-set.");
        }
        if (!TryParseJson(input.DecisionRulesJson, out var rulesError))
        {
            return Result<NewServiceProvisionDto>.Failure(ErrorCodes.ValidationFailed, $"DecisionRulesJson is invalid: {rulesError}");
        }

        var canonicalCode = input.Code.Trim().ToUpperInvariant();
        var canonicalWorkflow = input.WorkflowCode.Trim().ToUpperInvariant();

        // Duplicate-code guard — provision is create-only, not upsert. An existing row
        // for the code (current or historical) short-circuits with Conflict so the caller
        // routes through the regular IServicePassportService.UpsertAsync surface to add
        // a new version instead.
        var existing = await _db.ServicePassports
            .AnyAsync(p => p.Code == canonicalCode, cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            return Result<NewServiceProvisionDto>.Failure(
                ErrorCodes.Conflict,
                $"A service passport with code '{canonicalCode}' is already registered.");
        }

        // Workflow placeholder seeding — when the referenced workflow does not yet have a
        // current definition, persist an empty `{}` placeholder so the catalogue row never
        // points at a dangling code. SaveDefinitionAsync is a no-op when the body is
        // byte-equal to the current one, so re-runs are idempotent.
        var workflowProbe = await _workflows
            .GetDefinitionAsync(canonicalWorkflow, cancellationToken)
            .ConfigureAwait(false);
        if (workflowProbe.IsFailure && workflowProbe.ErrorCode == ErrorCodes.NotFound)
        {
            var seed = await _workflows
                .SaveDefinitionAsync(canonicalWorkflow, "{}", cancellationToken)
                .ConfigureAwait(false);
            if (seed.IsFailure)
            {
                return Result<NewServiceProvisionDto>.Failure(seed.ErrorCode!, seed.ErrorMessage!);
            }
        }

        var now = _clock.UtcNow;
        var passport = new ServicePassport
        {
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            Code = canonicalCode,
            NameRo = input.NameRo,
            NameEn = input.NameEn,
            NameRu = input.NameRu,
            DescriptionRo = input.DescriptionRo,
            FormSchemaJson = input.FormSchemaJson,
            WorkflowCode = canonicalWorkflow,
            MaxProcessingDays = input.MaxProcessingDays,
            IsEnabled = input.IsEnabled,
            IsProactive = input.IsProactive,
            DecisionRulesJson = input.DecisionRulesJson,
            Version = 1,
            IsCurrent = true,
            IsActive = true,
        };
        _db.ServicePassports.Add(passport);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit — Critical so MLog dual-write picks it up (CF 23.07 / SEC 055).
        var details = JsonSerializer.Serialize(new
        {
            code = canonicalCode,
            workflow = canonicalWorkflow,
            classifierSchemes = input.ClassifierSchemes,
            isEnabled = input.IsEnabled,
            isProactive = input.IsProactive,
        });
        await _audit.RecordAsync(
            eventCode: ProvisionedEvent,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ServicePassport),
            targetEntityId: passport.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<NewServiceProvisionDto>.Success(new NewServiceProvisionDto(
            Id: _sqids.Encode(passport.Id),
            Code: canonicalCode,
            WorkflowCode: canonicalWorkflow,
            Version: passport.Version));
    }

    /// <inheritdoc />
    public async Task<Result> RetireAsync(
        string passportCode,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passportCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Passport code is required.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Reason is required.");
        }

        var canonical = passportCode.Trim().ToUpperInvariant();

        var current = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Code == canonical && p.IsCurrent && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Result.Failure(ErrorCodes.NotFound, $"No active service passport found for code '{canonical}'.");
        }

        if (!current.IsEnabled)
        {
            // Already retired — idempotent success without an extra audit row.
            return Result.Success();
        }

        var now = _clock.UtcNow;
        current.IsEnabled = false;
        current.UpdatedAtUtc = now;
        current.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            code = canonical,
            reason,
        });
        await _audit.RecordAsync(
            eventCode: RetiredEvent,
            severity: AuditSeverity.Critical,
            actorId: _caller.UserSqid ?? "system",
            targetEntity: nameof(ServicePassport),
            targetEntityId: current.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="json"/> parses as a JSON object
    /// that declares ≥1 entry under a <c>properties</c> object. Surfaces a human-readable
    /// reason in <paramref name="error"/> when the shape is unacceptable.
    /// </summary>
    /// <param name="json">Raw JSON payload.</param>
    /// <param name="error">Diagnostic message; <see langword="null"/> on success.</param>
    /// <returns>True when the schema declares at least one form field.</returns>
    private static bool TryParseJsonObject(string json, out string? error)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "FormSchemaJson must not be empty.";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "FormSchemaJson must be a JSON object.";
                return false;
            }
            if (!doc.RootElement.TryGetProperty("properties", out var props)
                || props.ValueKind != JsonValueKind.Object
                || !props.EnumerateObject().Any())
            {
                error = "FormSchemaJson must declare at least one form field under 'properties'.";
                return false;
            }
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="json"/> parses as any
    /// well-formed JSON document (object, array, primitive). Surfaces a human-readable
    /// reason in <paramref name="error"/> on parse failure.
    /// </summary>
    /// <param name="json">Raw JSON payload.</param>
    /// <param name="error">Diagnostic message; <see langword="null"/> on success.</param>
    /// <returns>True when parseable.</returns>
    private static bool TryParseJson(string json, out string? error)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
