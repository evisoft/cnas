using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0122 / TOR CF 16.07 — unit tests for
/// <see cref="WorkflowPerformerAssignmentDtoValidator"/>. The validator gates the
/// shape of a workflow performer assignment before any handler / engine touches it:
/// <list type="bullet">
///   <item><c>Kind</c> must parse to a known <c>WorkflowPerformerKind</c>;</item>
///   <item><c>Kind=Role</c> requires <c>Code</c> matching a known
///   <see cref="RoleCodes"/> entry;</item>
///   <item><c>Kind=NamedUser</c> requires <c>Code</c> to be a Sqid that decodes
///   successfully;</item>
///   <item>reflexive kinds (Originator / Supervisor) accept a null code.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 — tests written FIRST (RED) before the validator class lands.
/// </remarks>
public sealed class WorkflowPerformerAssignmentValidatorTests
{
    private static ISqidService NewSqidStub(bool decodeOk = true)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            if (!decodeOk) return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            var s = call.Arg<string>();
            if (string.IsNullOrWhiteSpace(s)) return Result<long>.Failure(ErrorCodes.InvalidSqid, "empty");
            return Result<long>.Success(42L);
        });
        return sqids;
    }

    [Fact]
    public async Task ValidRole_Accepted()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub());
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "Role",
            Code: RoleCodes.Decider);

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownRoleCode_Rejected()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub());
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "Role",
            Code: "not-a-real-role");

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Role", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RoleKindMissingCode_Rejected()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub());
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "Role",
            Code: null);

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task NamedUserKind_SqidDecodes_Accepted()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub(decodeOk: true));
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "NamedUser",
            Code: "SQID-VALID");

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task NamedUserKind_SqidDoesNotDecode_Rejected()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub(decodeOk: false));
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "NamedUser",
            Code: "not-a-sqid");

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Sqid", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OriginatorKind_AcceptsNullCode()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub());
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "Originator",
            Code: null);

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownKindString_Rejected()
    {
        var v = new WorkflowPerformerAssignmentDtoValidator(NewSqidStub());
        var dto = new WorkflowPerformerAssignmentDto(
            Kind: "BogusKind",
            Code: "anything");

        var result = await v.ValidateAsync(dto, CancellationToken.None);

        result.IsValid.Should().BeFalse();
    }
}
