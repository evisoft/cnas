namespace Cnas.Ps.Contracts;

/// <summary>
/// UC20 — Schedule update request. Body of <c>PUT /api/automation/{code}/schedule</c>.
/// Carries a single field — a standard Quartz cron expression — that the technical
/// administrator wants to set as the new firing schedule for the named automation. The
/// route segment <c>{code}</c> identifies WHICH automation to retune; this payload only
/// carries the new firing rule.
/// </summary>
/// <param name="CronExpression">
/// A Quartz-compatible cron expression (6 or 7 fields:
/// <c>second minute hour day-of-month month day-of-week [year]</c>) describing the new
/// firing schedule. The value is forwarded verbatim to
/// <c>Cnas.Ps.Application.UseCases.IAutomationService.ScheduleAsync</c> which validates
/// the syntax against Quartz's parser; malformed expressions surface as
/// <c>VALIDATION_FAILED</c>. Never empty.
/// </param>
public sealed record AutomationScheduleRequest(string CronExpression);

/// <summary>
/// UC20 — On-demand automation run request. Body of
/// <c>POST /api/automation/{code}/run-now</c>. Optional parameter map serialised to a
/// JSON object the automation can read out of its Quartz JobDataMap. When omitted (or
/// passed as <c>null</c>), the controller forwards the literal <c>"{}"</c> so the
/// automation sees an empty parameter map rather than a parse error.
/// </summary>
/// <param name="Parameters">
/// Optional dictionary of automation-specific parameters. Each key/value pair is
/// serialised as a JSON property on the wire (values may be <c>null</c>). Defaults to
/// <c>null</c> (no parameters).
/// </param>
public sealed record AutomationRunNowRequest(IReadOnlyDictionary<string, string?>? Parameters = null);
