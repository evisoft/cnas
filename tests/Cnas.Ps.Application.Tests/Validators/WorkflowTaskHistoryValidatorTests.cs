using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0125 / CF 16.09 — unit tests for the workflow-task history filter validator.
/// </summary>
public sealed class WorkflowTaskHistoryValidatorTests
{
    [Fact]
    public void HappyPath_EmptyFilter_Accepted()
    {
        var v = new WorkflowTaskHistoryFilterDtoValidator();
        v.Validate(new WorkflowTaskHistoryFilterDto()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void GoodEventKind_Accepted()
    {
        var v = new WorkflowTaskHistoryFilterDtoValidator();
        v.Validate(new WorkflowTaskHistoryFilterDto(EventKind: "Reassigned"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void BadEventKind_Rejected()
    {
        var v = new WorkflowTaskHistoryFilterDtoValidator();
        v.Validate(new WorkflowTaskHistoryFilterDto(EventKind: "Bogus")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TakeOutOfRange_Rejected()
    {
        var v = new WorkflowTaskHistoryFilterDtoValidator();
        v.Validate(new WorkflowTaskHistoryFilterDto(Take: 0)).IsValid.Should().BeFalse();
        v.Validate(new WorkflowTaskHistoryFilterDto(Take: 500)).IsValid.Should().BeFalse();
    }
}
