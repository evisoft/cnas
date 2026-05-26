using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0381 / CF 16.11 — unit tests for <see cref="WorkflowTaskReassignDtoValidator"/>
/// when driven via the supervisor surface (the spec's <c>TaskReassignInputDto</c>
/// alias shares the same shape and routes through the same validator). Pins the
/// 3..500-character envelope on <c>Reason</c> plus the <c>NewAssigneeSqid</c>
/// required-rule.
/// </summary>
/// <remarks>
/// Written test-first per CLAUDE.md RULE 1 — these failed until the
/// reassignment validator landed (the validator was already shipped under R0127;
/// the R0381 spec keeps it as the canonical validator for the supervisor body).
/// </remarks>
public sealed class TaskReassignInputValidatorTests
{
    [Fact]
    public void HappyPath_ValidSqidAndReason_PassesValidation()
    {
        // Arrange — minimal valid payload at the lower bound of the reason length.
        var validator = new WorkflowTaskReassignDtoValidator();
        var dto = new WorkflowTaskReassignDto(NewAssigneeSqid: "k3Gq9", Reason: "ill");

        // Act
        var result = validator.Validate(dto);

        // Assert
        result.IsValid.Should().BeTrue(string.Join(";",
            result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void EmptyReason_FailsValidation()
    {
        var validator = new WorkflowTaskReassignDtoValidator();
        var dto = new WorkflowTaskReassignDto(NewAssigneeSqid: "k3Gq9", Reason: "");

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(WorkflowTaskReassignDto.Reason));
    }

    [Fact]
    public void TooShortReason_FailsValidation()
    {
        // 2 chars — below the 3-char floor.
        var validator = new WorkflowTaskReassignDtoValidator();
        var dto = new WorkflowTaskReassignDto(NewAssigneeSqid: "k3Gq9", Reason: "no");

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(WorkflowTaskReassignDto.Reason));
    }

    [Fact]
    public void TooLongReason_FailsValidation()
    {
        // 501 chars — above the 500-char ceiling that mirrors the column cap.
        var validator = new WorkflowTaskReassignDtoValidator();
        var tooLong = new string('x', 501);
        var dto = new WorkflowTaskReassignDto(NewAssigneeSqid: "k3Gq9", Reason: tooLong);

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(WorkflowTaskReassignDto.Reason));
    }

    [Fact]
    public void MissingNewAssignee_FailsValidation()
    {
        var validator = new WorkflowTaskReassignDtoValidator();
        var dto = new WorkflowTaskReassignDto(NewAssigneeSqid: "", Reason: "valid reason");

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(WorkflowTaskReassignDto.NewAssigneeSqid));
    }
}
