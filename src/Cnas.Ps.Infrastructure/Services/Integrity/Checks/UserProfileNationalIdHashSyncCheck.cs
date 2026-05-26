using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Integrity.Checks;

/// <summary>
/// R2282 / TOR SEC 036 — invariant: every <see cref="UserProfile"/> with a
/// non-null <c>NationalId</c> MUST also have a non-null <c>NationalIdHash</c>.
/// The shadow column backs equality lookups against the encrypted column;
/// when it is missing, IDNP-based lookups silently return zero results, which
/// directly breaks login/identity flows.
/// </summary>
public sealed class UserProfileNationalIdHashSyncCheck : IIntegrityCheck
{
    /// <inheritdoc />
    public string CheckCode => "USER_PROFILE.NATIONAL_ID_HASH_MISSING";

    /// <inheritdoc />
    public string AggregateName => nameof(UserProfile);

    /// <inheritdoc />
    public IntegrityFindingSeverity Severity => IntegrityFindingSeverity.Critical;

    /// <inheritdoc />
    public async Task<IntegrityCheckPartialResult> RunAsync(
        IIntegrityCheckContext ctx,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Materialise only the projection we need so the encrypted NationalId
        // never reaches the integrity job (defence-in-depth — the finding row
        // must not carry the plaintext).
        var rows = await ctx.Db.UserProfiles
            .Where(u => u.IsActive)
            .Select(u => new
            {
                u.Id,
                NationalIdIsSet = u.NationalId != null,
                NationalIdHashIsSet = u.NationalIdHash != null,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findings = new List<IntegrityCheckFindingRecord>();
        foreach (var row in rows)
        {
            if (row.NationalIdIsSet && !row.NationalIdHashIsSet)
            {
                findings.Add(new IntegrityCheckFindingRecord(
                    CheckCode: CheckCode,
                    Severity: Severity,
                    AggregateName: AggregateName,
                    AggregateRowId: row.Id,
                    Description: "UserProfile carries a NationalId but the NationalIdHash shadow column is null — IDNP equality lookups will silently miss this row.",
                    ExpectedValue: "NationalIdHash != null when NationalId != null",
                    ActualValue: "NationalIdHash=null"));
            }
        }

        return new IntegrityCheckPartialResult(rows.Count, findings);
    }
}
