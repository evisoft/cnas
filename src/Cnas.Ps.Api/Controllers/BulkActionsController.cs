using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.BulkActions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — REST surface for the cross-page bulk-action stack.
/// A user creates a <c>BulkSelection</c> (registry + filter envelope + optional include /
/// exclude id lists), reads back the resolved-count preview, and later submits a
/// <c>BulkOperationRun</c> consuming the selection. The discovery endpoint
/// (<c>GET /api/bulk-actions/operations</c>) lets a UI render the per-registry catalog
/// of available operations without hard-coding the set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route table.</b>
/// <list type="bullet">
///   <item><c>POST /api/bulk-actions/selections</c>              — create selection (201 + Location header).</item>
///   <item><c>GET  /api/bulk-actions/selections/{sqid}</c>       — fetch selection state.</item>
///   <item><c>GET  /api/bulk-actions/operations</c>              — list registered operations.</item>
///   <item><c>POST /api/bulk-actions/runs</c>                    — create + execute a run.</item>
///   <item><c>GET  /api/bulk-actions/runs/{sqid}</c>             — fetch run state.</item>
/// </list>
/// </para>
/// <para>
/// <b>Error-code → HTTP status mapping.</b>
/// <see cref="ErrorCodes.NotFound"/> → 404,
/// <see cref="ErrorCodes.Forbidden"/> → 403,
/// <see cref="ErrorCodes.Unauthorized"/> → 401,
/// <see cref="ErrorCodes.BulkSelectionExpired"/> / <see cref="ErrorCodes.BulkSelectionConsumed"/> → 409,
/// <see cref="ErrorCodes.BulkOperationUnknown"/> → 404,
/// <see cref="ErrorCodes.BulkQuotaExceeded"/>, <see cref="ErrorCodes.ValidationFailed"/>,
/// <see cref="ErrorCodes.InvalidSqid"/>, <see cref="ErrorCodes.InvalidId"/> → 400.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationComposition.CnasUser)]
[EnableRateLimiting(RateLimitingPolicies.Authenticated)]
[Route("api/bulk-actions")]
public sealed class BulkActionsController : ControllerBase
{
    private readonly IBulkSelectionService _selections;
    private readonly IBulkOperationRunner _runner;
    private readonly IBulkOperationRegistry _registry;
    private readonly ISqidService _sqids;

    /// <summary>Constructs the controller with its three services.</summary>
    /// <param name="selections">Bulk-selection lifecycle service.</param>
    /// <param name="runner">Bulk-operation runner.</param>
    /// <param name="registry">Operation registry consulted by the discovery endpoint.</param>
    /// <param name="sqids">Sqid encoder used to decode caller-supplied include / exclude ids.</param>
    public BulkActionsController(
        IBulkSelectionService selections,
        IBulkOperationRunner runner,
        IBulkOperationRegistry registry,
        ISqidService sqids)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sqids);

        _selections = selections;
        _runner = runner;
        _registry = registry;
        _sqids = sqids;
    }

    /// <summary>
    /// Creates a new bulk selection owned by the caller. Decodes the caller-supplied
    /// Sqid include / exclude lists, runs the per-registry filter resolver against
    /// the live DB, and persists the selection with an expiry stamp.
    /// </summary>
    /// <param name="input">Create payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 with the <see cref="BulkSelectionOutputDto"/> and a <c>Location</c>
    /// header pointing at the new row. Failures map per the class-level table.
    /// </returns>
    [HttpPost("selections")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateSelectionAsync(
        [FromBody] BulkSelectionCreateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Decode the caller-supplied Sqid lists into raw primary keys. A malformed
        // entry yields a 400 with the InvalidId code; the service trusts what it
        // receives.
        if (!TryDecodeIds(input.ExplicitIncludeIds, out var include, out var includeFailure))
        {
            return Problem(includeFailure, statusCode: StatusCodes.Status400BadRequest);
        }
        if (!TryDecodeIds(input.ExplicitExcludeIds, out var exclude, out var excludeFailure))
        {
            return Problem(excludeFailure, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _selections.CreateAsync(
            input.Registry,
            input.FilterJson,
            include,
            exclude,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }
        return CreatedAtAction(
            nameof(GetSelectionAsync),
            new { sqid = result.Value.Id },
            result.Value);
    }

    /// <summary>Fetches a single bulk selection by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded selection id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the selection on success; 403 / 404 on failure.</returns>
    [HttpGet("selections/{sqid}")]
    public async Task<IActionResult> GetSelectionAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _selections.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Lists every registered bulk operation as a descriptor DTO. The output is
    /// the catalog a UI uses to render per-registry action menus.
    /// </summary>
    /// <returns>200 with the unordered descriptor list.</returns>
    [HttpGet("operations")]
    public IActionResult ListOperations()
    {
        var descriptors = _registry.List()
            .Select(op => new BulkOperationDescriptorDto(
                op.Code,
                op.Registry,
                op.RequiredPermission,
                op.MaxRowsPerRun,
                op.RequiresParameters))
            .ToList();
        return Ok(descriptors);
    }

    /// <summary>
    /// Submits a bulk-operation run against a previously-persisted selection. The
    /// runner is idempotent on <see cref="BulkOperationRunCreateDto.IdempotencyKey"/>
    /// — a duplicate submit on the same (caller, code, key) tuple returns the prior
    /// outcome without re-executing the operation.
    /// </summary>
    /// <param name="input">Run-create payload (required body).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// 201 with the <see cref="BulkOperationRunOutputDto"/> and a <c>Location</c>
    /// header pointing at the new row. Failures map per the class-level table.
    /// </returns>
    [HttpPost("runs")]
    [Consumes("application/json")]
    public async Task<IActionResult> CreateRunAsync(
        [FromBody] BulkOperationRunCreateDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decoded = _sqids.TryDecode(input.BulkSelectionId);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _runner.RunAsync(
            decoded.Value,
            input.OperationCode,
            input.ParametersJson,
            input.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return MapFailureBare(result.ErrorCode, result.ErrorMessage);
        }
        return CreatedAtAction(
            nameof(GetRunAsync),
            new { sqid = result.Value.Id },
            result.Value);
    }

    /// <summary>Fetches a single bulk-operation run by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>200 with the run on success; 403 / 404 on failure.</returns>
    [HttpGet("runs/{sqid}")]
    public async Task<IActionResult> GetRunAsync(
        [FromRoute] string sqid,
        CancellationToken cancellationToken = default)
    {
        var result = await _runner.GetAsync(sqid, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Ok(result.Value)
            : MapFailureBare(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Decodes a caller-supplied Sqid list to raw primary keys. Returns false (with a
    /// human-readable failure message) on the first malformed entry; the caller maps
    /// the failure to a 400 with the <see cref="ErrorCodes.InvalidId"/> shape.
    /// </summary>
    /// <param name="sqids">Caller-supplied Sqid list (may be null or empty).</param>
    /// <param name="decoded">The decoded id list when the method returns true.</param>
    /// <param name="failure">A non-null human-readable message when the method returns false.</param>
    /// <returns>True when every entry decoded; false on the first malformed entry.</returns>
    private bool TryDecodeIds(
        IReadOnlyList<string>? sqids,
        out List<long> decoded,
        out string? failure)
    {
        decoded = new List<long>();
        failure = null;
        if (sqids is null || sqids.Count == 0)
        {
            return true;
        }
        foreach (var sqid in sqids)
        {
            var d = _sqids.TryDecode(sqid);
            if (d.IsFailure)
            {
                failure = $"{ErrorCodes.InvalidId}: '{sqid}' is not a valid id.";
                decoded.Clear();
                return false;
            }
            decoded.Add(d.Value);
        }
        return true;
    }

    /// <summary>Maps a non-generic <see cref="Result"/> failure to an <see cref="IActionResult"/>.</summary>
    /// <param name="code">Stable error code from <see cref="ErrorCodes"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <returns>The mapped ProblemDetails / NotFound action result.</returns>
    private IActionResult MapFailureBare(string? code, string? message)
    {
        var status = StatusForCode(code);
        return status == StatusCodes.Status404NotFound
            ? NotFound()
            : Problem(message, statusCode: status);
    }

    /// <summary>Translates a stable <see cref="ErrorCodes"/> value to an HTTP status code.</summary>
    /// <param name="code">Stable error code; null/unknown maps to 400.</param>
    /// <returns>Mapped HTTP status code.</returns>
    private static int StatusForCode(string? code) => code switch
    {
        ErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ErrorCodes.BulkOperationUnknown => StatusCodes.Status404NotFound,
        ErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorCodes.BulkSelectionConsumed => StatusCodes.Status409Conflict,
        ErrorCodes.BulkSelectionExpired => StatusCodes.Status409Conflict,
        ErrorCodes.Conflict => StatusCodes.Status409Conflict,
        ErrorCodes.ValidationFailed => StatusCodes.Status400BadRequest,
        ErrorCodes.BulkQuotaExceeded => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidSqid => StatusCodes.Status400BadRequest,
        ErrorCodes.InvalidId => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };
}
