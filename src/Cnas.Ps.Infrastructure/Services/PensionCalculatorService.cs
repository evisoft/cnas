using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Pension;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0514 / TOR CF 02.02 — implementation of
/// <see cref="IPensionCalculatorService"/>. Deterministic linear projection
/// gated by <see cref="PensionSimulationInputValidator"/>. The richer TOR §4.2
/// formula is deferred — see the iteration scope memo.
/// </summary>
public sealed class PensionCalculatorService : IPensionCalculatorService
{
    /// <summary>Audit event code emitted on every successful simulation.</summary>
    public const string AuditEventCode = "PUBLIC.PENSION_SIMULATION";

    /// <summary>Permission code that unlocks the coefficient-override slot.</summary>
    public const string AdvancedPermission = "Pension.SimulateAdvanced";

    private readonly IValidator<PensionSimulationInputDto> _validator;
    private readonly PensionOptions _options;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="validator">FluentValidation validator for the input DTO.</param>
    /// <param name="options">Pension-formula configuration values.</param>
    /// <param name="caller">Per-request caller context — used for the audit row + permission check.</param>
    /// <param name="audit">Audit-log façade.</param>
    public PensionCalculatorService(
        IValidator<PensionSimulationInputDto> validator,
        IOptions<PensionOptions> options,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _validator = validator;
        _options = options.Value;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<PensionSimulationDto>> SimulateAsync(
        PensionSimulationInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Defense in depth — the controller carries [Authorize], but internal
        // callers could bypass it. Anonymous callers cannot run the simulator.
        if (_caller.UserId is null)
        {
            return Result<PensionSimulationDto>.Failure(
                ErrorCodes.Unauthorized,
                "Pension simulation requires an authenticated caller.");
        }

        // 1. Validate the input bounds before touching any business logic.
        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            return Result<PensionSimulationDto>.Failure(
                ErrorCodes.ValidationFailed,
                first.ErrorMessage);
        }

        // 2. Resolve the effective accrual coefficient. The override is
        //    honoured only when the caller carries the advanced permission;
        //    otherwise we silently fall back to the configured default so the
        //    DTO shape can stay constant across roles.
        var hasAdvancedPermission = _caller.Roles.Contains(AdvancedPermission, StringComparer.Ordinal);
        var accrualCoefficient = hasAdvancedPermission && input.CoefficientOverride.HasValue
            ? input.CoefficientOverride.Value
            : _options.DefaultAccrualCoefficient;

        // 3. Resolve the effective retirement age — caller-supplied value when
        //    present, otherwise the gender default.
        var effectiveRetirementAge = input.RetirementAge
            ?? (string.Equals(input.Gender, "F", StringComparison.Ordinal)
                ? _options.DefaultFemaleRetirementAge
                : _options.DefaultMaleRetirementAge);

        var yearsUntilRetirement = Math.Max(0, effectiveRetirementAge - input.CurrentAge);

        // 4. Apply the linear formula: base × coefficient/100 × years.
        //    Round to two decimals at the boundary so the floor comparison and
        //    the formula-description echo carry the same value the citizen
        //    sees in the UI.
        var rawAmount = Math.Round(
            input.AverageMonthlyContributionBase * (accrualCoefficient / 100m) * input.YearsOfService,
            2,
            MidpointRounding.AwayFromZero);

        var floor = _options.MinPensionFloor;
        var floorApplied = rawAmount < floor;
        var finalAmount = floorApplied ? floor : rawAmount;

        // 5. Human-readable explanation — Romanian text plus the substituted
        //    numbers. Invariant-culture formatting keeps the decimal point
        //    stable across runtimes (the UI re-formats for the user locale).
        var ci = CultureInfo.InvariantCulture;
        var baseDescription = string.Format(
            ci,
            "{0:F2} MDL × {1:F2}% × {2} ani = {3:F2} MDL",
            input.AverageMonthlyContributionBase,
            accrualCoefficient,
            input.YearsOfService,
            rawAmount);
        var description = floorApplied
            ? $"{baseDescription}; aplicat plafonul minim {floor.ToString("F2", ci)} MDL"
            : baseDescription;

        var dto = new PensionSimulationDto(
            EstimatedMonthlyPension: finalAmount,
            YearsUntilRetirement: yearsUntilRetirement,
            AccrualCoefficient: accrualCoefficient,
            MinPensionFloor: floor,
            FloorApplied: floorApplied,
            FormulaDescriptionRo: description);

        // 6. Audit row — Information severity (read-only projection, no PII).
        var details = JsonSerializer.Serialize(new
        {
            yearsOfService = input.YearsOfService,
            averageMonthlyContributionBase = input.AverageMonthlyContributionBase,
            currentAge = input.CurrentAge,
            retirementAge = effectiveRetirementAge,
            gender = input.Gender,
            accrualCoefficient,
            result = finalAmount,
            floorApplied,
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: null,
            targetEntityId: null,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<PensionSimulationDto>.Success(dto);
    }
}
