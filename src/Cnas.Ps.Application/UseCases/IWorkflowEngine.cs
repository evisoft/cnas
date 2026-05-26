using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// Abstraction over the BPMN 2.0 workflow engine that drives long-running CNAS business
/// processes (application intake, dossier examination, decision approval). The reference
/// implementation targets Operaton (the open-source Camunda 7 fork) but the contract is
/// engine-agnostic — variables flow as a loosely-typed bag and process state is exposed as
/// a <see cref="WorkflowInstance"/> projection.
/// </summary>
/// <remarks>
/// All methods return <see cref="Result"/>/<see cref="Result{T}"/>: transport-layer or upstream
/// failures map to <c>WORKFLOW_ENGINE_FAILED</c>; configuration-layer issues (no base URL)
/// map to <c>INTERNAL_ERROR</c>. The engine never throws for business outcomes.
/// </remarks>
public interface IWorkflowEngine
{
    /// <summary>
    /// Starts a process instance for the given BPMN definition key with optional variables.
    /// </summary>
    /// <param name="processKey">The BPMN process-definition key (e.g. <c>"DOSSIER_INTAKE"</c>).</param>
    /// <param name="variables">Initial process variables; keys must match the BPMN model.</param>
    /// <param name="ct">Cancellation token bound to the inbound HTTP request.</param>
    /// <returns>A <see cref="Result{T}"/> wrapping the freshly started <see cref="WorkflowInstance"/>.</returns>
    Task<Result<WorkflowInstance>> StartProcessAsync(
        string processKey,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default);

    /// <summary>
    /// Completes an external task or user task with the given task identifier, optionally
    /// passing variables back into the process. Returns the next state of the process.
    /// </summary>
    /// <param name="taskId">The engine-assigned task id (Operaton: 22-char UUID).</param>
    /// <param name="variables">Variables to merge into the process scope on completion.</param>
    /// <param name="ct">Cancellation token bound to the inbound HTTP request.</param>
    Task<Result> CompleteTaskAsync(
        string taskId,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default);

    /// <summary>Gets the current state of a process instance.</summary>
    /// <param name="instanceId">Process instance id assigned by the engine on start.</param>
    /// <param name="ct">Cancellation token bound to the inbound HTTP request.</param>
    Task<Result<WorkflowInstance>> GetInstanceAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Cancels (deletes) a running process instance with the given reason.</summary>
    /// <param name="instanceId">Process instance id assigned by the engine on start.</param>
    /// <param name="reason">Free-text reason recorded by the engine for audit purposes.</param>
    /// <param name="ct">Cancellation token bound to the inbound HTTP request.</param>
    Task<Result> CancelInstanceAsync(string instanceId, string reason, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of a workflow process instance as exposed by the engine adapter.
/// </summary>
/// <param name="InstanceId">Engine-assigned process instance id (opaque to callers).</param>
/// <param name="DefinitionKey">BPMN process-definition key the instance was started from.</param>
/// <param name="Status">One of <c>"Active"</c>, <c>"Completed"</c>, <c>"Cancelled"</c>, <c>"Suspended"</c>.</param>
/// <param name="StartedAtUtc">UTC instant when the process was started.</param>
/// <param name="EndedAtUtc">UTC instant when the process ended, or <c>null</c> while still active.</param>
/// <param name="ActiveTasks">Tasks currently waiting on a human or external worker.</param>
public sealed record WorkflowInstance(
    string InstanceId,
    string DefinitionKey,
    string Status,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    IReadOnlyList<WorkflowActiveTask> ActiveTasks);

/// <summary>
/// Lightweight projection of an active task on a running process instance.
/// </summary>
/// <param name="TaskId">Engine-assigned task id (Operaton: 22-char UUID).</param>
/// <param name="Name">Human-friendly task name from the BPMN definition.</param>
/// <param name="AssigneeGroup">Candidate group the task is currently assigned to.</param>
/// <param name="DueAtUtc">Optional due-date for the task, in UTC.</param>
public sealed record WorkflowActiveTask(
    string TaskId,
    string Name,
    string AssigneeGroup,
    DateTime? DueAtUtc);
