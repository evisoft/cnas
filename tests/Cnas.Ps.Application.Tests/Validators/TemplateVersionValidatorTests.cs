using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0132 / CF 17.18 — unit tests for the template rollback input validator.
/// </summary>
public sealed class TemplateVersionValidatorTests
{
    [Fact]
    public void HappyPath_GoodReason_Accepted()
    {
        var v = new TemplateRollbackInputDtoValidator();
        v.Validate(new TemplateRollbackInputDto("Reverting after broken merge.")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyReason_Rejected()
    {
        var v = new TemplateRollbackInputDtoValidator();
        v.Validate(new TemplateRollbackInputDto("")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TooShortReason_Rejected()
    {
        var v = new TemplateRollbackInputDtoValidator();
        v.Validate(new TemplateRollbackInputDto("ab")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TooLongReason_Rejected()
    {
        var v = new TemplateRollbackInputDtoValidator();
        var huge = new string('x', TemplateRollbackInputDtoValidator.MaxReasonLength + 1);
        v.Validate(new TemplateRollbackInputDto(huge)).IsValid.Should().BeFalse();
    }
}
