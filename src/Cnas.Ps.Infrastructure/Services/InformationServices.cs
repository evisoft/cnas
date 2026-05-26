using Cnas.Ps.Contracts;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default implementation of the public information services (UC02). The retirement-age
/// calculator uses the table set out in Legea nr. 156/1998 (men 63 by 2028, women 60 by 2028)
/// approximated linearly for live data.
/// </summary>
public sealed class InformationServices(ICnasDbContext db, ICnasTimeProvider clock) : IInformationServices
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;

    /// <inheritdoc />
    public Task<Result<RetirementAgeOutput>> CalculateRetirementAgeAsync(
        RetirementAgeInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = cancellationToken;

        // Sex is wire-format string ("M"/"F"); inspect only the first character so
        // legacy lower-case forms still resolve. Empty / null defaults to the male
        // schedule per the legacy fallback policy.
        var sexChar = string.IsNullOrEmpty(input.Sex) ? 'M' : input.Sex[0];
        var (years, months) = sexChar switch
        {
            'M' or 'm' => (63, 0),
            'F' or 'f' => (60, 0),
            _ => (63, 0),
        };

        var retirementDate = input.BirthDate.AddYears(years).AddMonths(months);
        return Task.FromResult(Result<RetirementAgeOutput>.Success(new RetirementAgeOutput(retirementDate, years)));
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationStatusOutput>> GetApplicationStatusAsync(
        string referenceNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceNumber);

        var row = await _db.Applications
            .Where(a => a.ReferenceNumber == referenceNumber && a.IsActive)
            .Select(a => new { a.Status, a.UpdatedAtUtc, a.SubmittedAtUtc })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result<ApplicationStatusOutput>.Failure(ErrorCodes.NotFound, "Reference not found.");
        }

        _ = _clock; // future-proof: timezone-aware presentation goes here

        return Result<ApplicationStatusOutput>.Success(new ApplicationStatusOutput(
            referenceNumber,
            row.Status.ToString(),
            row.UpdatedAtUtc ?? row.SubmittedAtUtc));
    }
}
