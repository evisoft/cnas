namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — operator-registered recurrent-payment schedule
/// driving the monthly state-support and similar monthly-allowance services
/// (3.2-Z Suport financiar de stat lunar). One row models a single
/// beneficiary's recurring obligation, including the cadence
/// (Monthly / Quarterly / Annual), the next due date, and the most recent
/// dispatch outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> — the schedule
/// surfaces a Sqid through the admin REST surface per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>Idempotency contract.</b> The companion <c>RecurrentPaymentJob</c>
/// re-fires only against schedules with <see cref="NextPaymentDate"/> on or
/// before the run date and <see cref="AuditableEntity.IsActive"/> true; on
/// dispatch the scheduler advances <see cref="NextPaymentDate"/> by one
/// cadence step so a second run on the same day is a no-op.
/// </para>
/// <para>
/// <b>Suspension semantics.</b> Suspending the schedule flips
/// <see cref="AuditableEntity.IsActive"/> to <c>false</c> (the schedule is
/// preserved for forensic lookups). Resume flips it back to <c>true</c>;
/// the run job resumes processing on the next due date.
/// </para>
/// </remarks>
public sealed class RecurrentPaymentSchedule : AuditableEntity, IExternalId
{
    /// <summary>
    /// Internal beneficiary id (raw <c>long</c>) the recurrent payment is
    /// addressed to. Never surfaced on the wire; the admin DTOs project
    /// <c>BeneficiarySqid</c>.
    /// </summary>
    public long BeneficiaryId { get; set; }

    /// <summary>
    /// Stable passport / service code this schedule disburses against
    /// (e.g. <c>3.2-Z</c>). Length capped at 32 characters at the
    /// persistence layer.
    /// </summary>
    public required string ServiceCode { get; set; }

    /// <summary>
    /// Per-payment amount in Moldovan Lei. Stored at <c>decimal(18, 2)</c>
    /// precision to match the rest of the financial columns in the schema.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Calendar date on which the next payment becomes due. The
    /// run-job picks up every row with <see cref="NextPaymentDate"/> ≤
    /// <c>today</c> and <see cref="AuditableEntity.IsActive"/> = <c>true</c>.
    /// </summary>
    public DateOnly NextPaymentDate { get; set; }

    /// <summary>Cadence step the scheduler advances by on each successful dispatch.</summary>
    public RecurrentPaymentCadence Cadence { get; set; }

    /// <summary>
    /// UTC instant the most recent dispatch completed; <c>null</c> until
    /// the first run.
    /// </summary>
    public DateTime? LastPaymentAtUtc { get; set; }

    /// <summary>
    /// Running count of consecutive failed dispatch attempts. Reset to
    /// <c>0</c> on a successful run; consumed by the
    /// operator-monitoring surface to flag schedules that need attention.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Database id of the last <see cref="MPayOrder"/> emitted by the
    /// run-due dispatcher for this schedule. <c>null</c> until the first
    /// dispatch lands. Read by the MPay callback advancer to determine
    /// whether a confirmed order should advance this schedule's
    /// <see cref="NextPaymentDate"/> by one cadence step. Stored as the raw
    /// long surrogate id because the column is internal-only — it never
    /// crosses the system boundary so no Sqid encoding is required.
    /// </summary>
    public long? LastDispatchedOrderId { get; set; }
}

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — cadence step the recurrent-payment scheduler
/// advances <see cref="RecurrentPaymentSchedule.NextPaymentDate"/> by on each
/// successful dispatch.
/// </summary>
public enum RecurrentPaymentCadence
{
    /// <summary>+1 calendar month per dispatch (3.2-Z and most monthly allowances).</summary>
    Monthly = 0,

    /// <summary>+3 calendar months per dispatch.</summary>
    Quarterly = 1,

    /// <summary>+12 calendar months per dispatch.</summary>
    Annual = 2,
}
