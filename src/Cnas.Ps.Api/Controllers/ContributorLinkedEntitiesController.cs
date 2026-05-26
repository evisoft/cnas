using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cnas.Ps.Api.Controllers;

/// <summary>
/// R0311 / ARH 028 / TOR Annex 2.3 — REST surface for InsuredPerson (Persoană asigurată)
/// linked child rows.
/// </summary>
/// <param name="svc">Underlying linked-entities service.</param>
/// <param name="sqids">Sqid encoder/decoder.</param>
[ApiController]
[Authorize(Roles = "cnas-user,cnas-admin")]
[Route("api/contributors/{contributorSqid}")]
public sealed class ContributorLinkedEntitiesController(
    IContributorLinkedEntitiesService svc,
    ISqidService sqids) : ControllerBase
{
    private readonly IContributorLinkedEntitiesService _svc = svc;
    private readonly ISqidService _sqids = sqids;

    /// <summary>Updates the current address.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Address payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("address")]
    public async Task<ActionResult<ContributorAddressDto>> PutAddressAsync(
        string contributorSqid,
        [FromBody] ContributorAddressInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateAddressAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<ContributorAddressDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Updates the current contact.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Contact payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("contact")]
    public async Task<ActionResult<ContributorContactDto>> PutContactAsync(
        string contributorSqid,
        [FromBody] ContributorContactInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateContactAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<ContributorContactDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Adds an activity period.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Activity payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("activity-periods")]
    public async Task<ActionResult<ContributorActivityPeriodDto>> PostActivityPeriodAsync(
        string contributorSqid,
        [FromBody] ContributorActivityPeriodInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.AddActivityPeriodAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<ContributorActivityPeriodDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Ends an activity period.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id (route consistency).</param>
    /// <param name="periodSqid">Sqid of the activity-period row.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("activity-periods/{periodSqid}")]
    public async Task<IActionResult> EndActivityPeriodAsync(
        string contributorSqid, string periodSqid,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        _ = contributorSqid;
        var decoded = _sqids.TryDecode(periodSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.EndActivityPeriodAsync(decoded.Value, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess ? NoContent() : MapFailureBare(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Updates the civil status.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Civil-status payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("civil-status")]
    public async Task<ActionResult<ContributorCivilStatusDto>> PutCivilStatusAsync(
        string contributorSqid,
        [FromBody] ContributorCivilStatusInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateCivilStatusAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<ContributorCivilStatusDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Updates the social-insurance contract.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Contract payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("social-insurance-contract")]
    public async Task<ActionResult<ContributorSocialInsuranceContractDto>> PutSocialInsuranceContractAsync(
        string contributorSqid,
        [FromBody] ContributorSocialInsuranceContractInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.UpdateSocialInsuranceContractAsync(decoded.Value, input, changeReason, ct)
            .ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<ContributorSocialInsuranceContractDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Adds a pre-1999 Carnet de muncă period.</summary>
    /// <param name="contributorSqid">Sqid-encoded InsuredPerson id.</param>
    /// <param name="input">Period payload.</param>
    /// <param name="changeReason">Optional rationale.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("pre-1999-periods")]
    public async Task<ActionResult<ContributorPre1999PeriodCarnetMuncaDto>> PostPre1999PeriodAsync(
        string contributorSqid,
        [FromBody] ContributorPre1999PeriodCarnetMuncaInputDto input,
        [FromQuery] string? changeReason = null,
        CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(contributorSqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await _svc.AddPre1999PeriodAsync(decoded.Value, input, changeReason, ct).ConfigureAwait(false);
        return res.IsSuccess
            ? Ok(res.Value)
            : MapFailure<ContributorPre1999PeriodCarnetMuncaDto>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Lists address history.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("addresses/history")]
    public async Task<ActionResult<IReadOnlyList<ContributorAddressDto>>> ListAddressHistoryAsync(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListAddressHistoryAsync, ct).ConfigureAwait(false);

    /// <summary>Lists contact history.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("contacts/history")]
    public async Task<ActionResult<IReadOnlyList<ContributorContactDto>>> ListContactHistoryAsync(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListContactHistoryAsync, ct).ConfigureAwait(false);

    /// <summary>Lists activity-period history.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("activity-periods/history")]
    public async Task<ActionResult<IReadOnlyList<ContributorActivityPeriodDto>>> ListActivityHistoryAsync(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListActivityPeriodHistoryAsync, ct).ConfigureAwait(false);

    /// <summary>Lists civil-status history.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("civil-statuses/history")]
    public async Task<ActionResult<IReadOnlyList<ContributorCivilStatusDto>>> ListCivilStatusHistoryAsync(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListCivilStatusHistoryAsync, ct).ConfigureAwait(false);

    /// <summary>Lists social-insurance contract history.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("social-insurance-contracts/history")]
    public async Task<ActionResult<IReadOnlyList<ContributorSocialInsuranceContractDto>>> ListContractHistoryAsync(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListSocialInsuranceContractHistoryAsync, ct).ConfigureAwait(false);

    /// <summary>Lists pre-1999 Carnet de muncă periods.</summary>
    /// <param name="contributorSqid">Sqid id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pre-1999-periods")]
    public async Task<ActionResult<IReadOnlyList<ContributorPre1999PeriodCarnetMuncaDto>>> ListPre1999Async(
        string contributorSqid, CancellationToken ct = default)
        => await ListAsync(contributorSqid, _svc.ListPre1999PeriodsAsync, ct).ConfigureAwait(false);

    /// <summary>Common list-handler wrapper: decodes the sqid then calls the supplied service function.</summary>
    /// <typeparam name="T">DTO type listed.</typeparam>
    /// <param name="sqid">Sqid id.</param>
    /// <param name="fn">Service method that lists rows.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<ActionResult<IReadOnlyList<T>>> ListAsync<T>(
        string sqid,
        Func<long, CancellationToken, Task<Result<IReadOnlyList<T>>>> fn,
        CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Problem(decoded.ErrorMessage, statusCode: 400);
        }
        var res = await fn(decoded.Value, ct).ConfigureAwait(false);
        return res.IsSuccess ? Ok(res.Value) : MapFailure<IReadOnlyList<T>>(res.ErrorCode, res.ErrorMessage);
    }

    /// <summary>Maps a failure to an HTTP response.</summary>
    /// <typeparam name="T">DTO type.</typeparam>
    /// <param name="code">Error code.</param>
    /// <param name="message">Detail.</param>
    private ActionResult<T> MapFailure<T>(string? code, string? message) =>
        code == ErrorCodes.NotFound ? NotFound() : Problem(message, statusCode: 400);

    /// <summary>Bare failure mapping.</summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Detail.</param>
    private IActionResult MapFailureBare(string? code, string? message) =>
        code == ErrorCodes.NotFound ? NotFound() : Problem(message, statusCode: 400);
}
