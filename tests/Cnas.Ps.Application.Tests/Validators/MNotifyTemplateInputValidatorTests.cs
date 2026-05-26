using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0115 / TOR CF 14.07 — pins the contract enforced by
/// <see cref="MNotifyTemplateInputValidator"/>.
/// </summary>
public sealed class MNotifyTemplateInputValidatorTests
{
    private readonly MNotifyTemplateInputValidator _sut = new();

    private static MNotifyTemplateInputDto NewValid() => new(
        Code: "WORKFLOW.TASK.ASSIGNED",
        ChannelKind: MNotifyChannelKindDto.Email,
        Subject: "New task",
        BodyMarkdown: "You have a new task assigned.");

    /// <summary>Happy path — a well-formed input is accepted.</summary>
    [Fact]
    public void Valid_Input_HasNoErrors()
    {
        var result = _sut.TestValidate(NewValid());
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Code shape is enforced.</summary>
    [Theory]
    [InlineData("lowercase")]
    [InlineData("Has Spaces")]
    [InlineData("")]
    public void Invalid_Code_Fails(string code)
    {
        var input = NewValid() with { Code = code };
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    /// <summary>Email templates without a subject are rejected.</summary>
    [Fact]
    public void Email_Without_Subject_Fails()
    {
        var input = NewValid() with { ChannelKind = MNotifyChannelKindDto.Email, Subject = null };
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.Subject);
    }

    /// <summary>SMS templates may omit the subject.</summary>
    [Fact]
    public void Sms_Without_Subject_IsAccepted()
    {
        var input = NewValid() with { ChannelKind = MNotifyChannelKindDto.Sms, Subject = null };
        var result = _sut.TestValidate(input);
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Body must be present.</summary>
    [Fact]
    public void Empty_Body_Fails()
    {
        var input = NewValid() with { BodyMarkdown = string.Empty };
        var result = _sut.TestValidate(input);
        result.ShouldHaveValidationErrorFor(x => x.BodyMarkdown);
    }
}
