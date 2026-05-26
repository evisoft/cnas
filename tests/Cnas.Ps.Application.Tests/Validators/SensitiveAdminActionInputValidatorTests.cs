using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R2273 / TOR SEC 027 — FluentValidation rules for the sensitive-admin-action DTOs.
/// Exercises the action-code regex, the reason length window, the payload JSON validity
/// + size cap, and the filter page bounds.
/// </summary>
public class SensitiveAdminActionInputValidatorTests
{
    private static SensitiveAdminActionRequestInputDto ValidRequest()
        => new(
            ActionCode: "USER.ROLE_GRANT",
            RequestReason: "Promoting Bob to admin for the weekend rota.",
            RequestPayloadJson: "{\"userId\":\"SQID-42\",\"role\":\"cnas-admin\"}");

    [Fact]
    public void Request_HappyPath_Passes()
    {
        var v = new SensitiveAdminActionRequestInputValidator();
        var result = v.Validate(ValidRequest());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Request_BadActionCodeShape_Rejected()
    {
        var v = new SensitiveAdminActionRequestInputValidator();
        var result = v.Validate(ValidRequest() with { ActionCode = "user.role_grant" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(SensitiveAdminActionRequestInputDto.ActionCode));
    }

    [Fact]
    public void Request_OversizedPayload_Rejected()
    {
        var v = new SensitiveAdminActionRequestInputValidator();
        // Build a valid JSON object whose UTF-8 size exceeds 8192 bytes. Inflate a single
        // string-property by repeating the letter 'a' enough to overflow the cap.
        var overflow = new string('a', 9000);
        var payload = "{\"blob\":\"" + overflow + "\"}";
        var result = v.Validate(ValidRequest() with { RequestPayloadJson = payload });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(SensitiveAdminActionRequestInputDto.RequestPayloadJson));
    }

    [Fact]
    public void Request_NonJsonPayload_Rejected()
    {
        var v = new SensitiveAdminActionRequestInputValidator();
        var result = v.Validate(ValidRequest() with { RequestPayloadJson = "not-a-json-document" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(SensitiveAdminActionRequestInputDto.RequestPayloadJson));
    }

    [Fact]
    public void Approval_NoteTooShort_Rejected()
    {
        var v = new SensitiveAdminActionApprovalInputValidator();
        var result = v.Validate(new SensitiveAdminActionApprovalInputDto("ok"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Reason_HappyPath_Passes()
    {
        var v = new SensitiveAdminActionReasonInputValidator();
        var result = v.Validate(new SensitiveAdminActionReasonInputDto("Operator changed mind — paperwork incomplete."));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Filter_TakeAboveMax_Rejected()
    {
        var v = new SensitiveAdminActionFilterValidator();
        var result = v.Validate(new SensitiveAdminActionFilterDto(Take: 500));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(SensitiveAdminActionFilterDto.Take));
    }

    [Fact]
    public void Filter_BadStatus_Rejected()
    {
        var v = new SensitiveAdminActionFilterValidator();
        var result = v.Validate(new SensitiveAdminActionFilterDto(Status: "bogus_status"));
        result.IsValid.Should().BeFalse();
    }
}
