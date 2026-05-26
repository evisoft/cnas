using System;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Core.ValueObjects;

/// <summary>
/// R0122 / TOR CF 16.07 — strongly-typed SLA descriptor for a workflow step. Replaces
/// the prior ad-hoc JSON shape (free-form <c>{"dueHours": 8, ...}</c>) with an
/// immutable, self-validating value object the engine + SLA monitor can consume
/// directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation contract.</b> Instances are produced exclusively through
/// <see cref="Create"/>, returning a <see cref="Result{T}"/>. The constructor is
/// private — there is no path to an invalid instance.
/// </para>
/// <para>
/// <b>Field semantics.</b>
/// <list type="bullet">
///   <item><c>DueWithin</c>: the deadline after which a task is considered late.
///   Must be strictly positive.</item>
///   <item><c>EscalateAfter</c>: the wall-clock duration after <c>DueWithin</c>
///   elapses at which an escalation should fire. Must be >= <c>DueWithin</c> so the
///   monitor doesn't escalate before the deadline has been missed.</item>
///   <item><c>BusinessHoursOnly</c>: when <c>true</c>, both deadlines are evaluated
///   against the configured business-hours calendar; when <c>false</c>, both clocks
///   run continuously (24×7).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class WorkflowStepSla
{
    private WorkflowStepSla(TimeSpan dueWithin, TimeSpan escalateAfter, bool businessHoursOnly)
    {
        DueWithin = dueWithin;
        EscalateAfter = escalateAfter;
        BusinessHoursOnly = businessHoursOnly;
    }

    /// <summary>Time budget from task creation to "must be completed by".</summary>
    public TimeSpan DueWithin { get; }

    /// <summary>Time budget from task creation to "escalate now"; >= <see cref="DueWithin"/>.</summary>
    public TimeSpan EscalateAfter { get; }

    /// <summary>When <c>true</c>, both windows are measured against the business-hours calendar.</summary>
    public bool BusinessHoursOnly { get; }

    /// <summary>
    /// Builds a validated <see cref="WorkflowStepSla"/> or returns a failure carrying
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <param name="dueWithin">Time-to-complete budget (must be > 0).</param>
    /// <param name="escalateAfter">Time-to-escalate budget (must be &gt;= <paramref name="dueWithin"/>).</param>
    /// <param name="businessHoursOnly">True to measure against business hours only.</param>
    /// <returns>Success with the constructed value, or a validation failure.</returns>
    public static Result<WorkflowStepSla> Create(
        TimeSpan dueWithin,
        TimeSpan escalateAfter,
        bool businessHoursOnly)
    {
        if (dueWithin <= TimeSpan.Zero)
        {
            return Result<WorkflowStepSla>.Failure(
                ErrorCodes.ValidationFailed,
                "DueWithin must be strictly positive.");
        }

        if (escalateAfter < dueWithin)
        {
            return Result<WorkflowStepSla>.Failure(
                ErrorCodes.ValidationFailed,
                "EscalateAfter must be greater than or equal to DueWithin.");
        }

        return Result<WorkflowStepSla>.Success(
            new WorkflowStepSla(dueWithin, escalateAfter, businessHoursOnly));
    }
}
