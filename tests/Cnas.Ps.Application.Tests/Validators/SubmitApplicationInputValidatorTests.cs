using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using FluentValidation.TestHelper;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="SubmitApplicationInputValidator"/> covering each field rule
/// and a happy-path "valid input" baseline (CLAUDE.md §3.3 — unit tier).
/// </summary>
public sealed class SubmitApplicationInputValidatorTests
{
    /// <summary>Single validator instance reused across tests (validators are thread-safe and stateless).</summary>
    private readonly SubmitApplicationInputValidator _validator = new();

    /// <summary>Two-element attachment id list with one empty entry to drive child NotEmpty failures.</summary>
    private static readonly string[] AttachmentsWithEmpty = { "doc12345", string.Empty };

    /// <summary>Single-element attachment id list with a too-short value to drive child Length failures.</summary>
    private static readonly string[] AttachmentsTooShort = { "abc" };

    /// <summary>Two valid attachment ids — used by the all-rules-pass happy-path test.</summary>
    private static readonly string[] AttachmentsValid = { "doc12345", "doc67890" };

    /// <summary>Builds a <see cref="SubmitApplicationInput"/> with reasonable defaults for tests to mutate.</summary>
    /// <param name="servicePassportId">Sqid of the requested service passport. Defaults to a valid 10-char id.</param>
    /// <param name="formPayloadJson">Form payload. Defaults to a minimal valid JSON object.</param>
    /// <param name="attachmentDocumentIds">Attachment Sqids. Defaults to an empty list.</param>
    private static SubmitApplicationInput BuildValid(
        string servicePassportId = "ssp1234567",
        string formPayloadJson = "{\"k\":1}",
        IReadOnlyList<string>? attachmentDocumentIds = null)
        => new(servicePassportId, formPayloadJson, attachmentDocumentIds ?? Array.Empty<string>());

    [Fact]
    public void ServicePassportId_Empty_Fails()
    {
        // Arrange: empty string violates both NotEmpty and Length(4,64).
        var input = BuildValid(servicePassportId: string.Empty);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ServicePassportId);
    }

    [Fact]
    public void ServicePassportId_TooShort_Fails()
    {
        // Arrange: 3 chars — below the Length(4,64) lower bound.
        var input = BuildValid(servicePassportId: "abc");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ServicePassportId);
    }

    [Fact]
    public void ServicePassportId_TooLong_Fails()
    {
        // Arrange: 65 chars — exceeds the Length(4,64) upper bound by one.
        var input = BuildValid(servicePassportId: new string('a', 65));

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.ServicePassportId);
    }

    [Fact]
    public void ServicePassportId_Valid_Passes()
    {
        // Arrange: 10 chars — comfortably inside [4,64].
        var input = BuildValid(servicePassportId: "ssp1234567");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.ServicePassportId);
    }

    [Fact]
    public void FormPayloadJson_Empty_Fails()
    {
        // Arrange: empty payload — fails NotEmpty + BeJsonObject.
        var input = BuildValid(formPayloadJson: string.Empty);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FormPayloadJson);
    }

    [Fact]
    public void FormPayloadJson_Whitespace_Fails()
    {
        // Arrange: whitespace-only payload — BeJsonObject returns false for IsNullOrWhiteSpace.
        var input = BuildValid(formPayloadJson: "   ");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FormPayloadJson);
    }

    [Fact]
    public void FormPayloadJson_NotJsonObject_Fails()
    {
        // Arrange: a JSON array is valid JSON but not a JSON object — must be rejected.
        var input = BuildValid(formPayloadJson: "[1,2]");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FormPayloadJson);
    }

    [Fact]
    public void FormPayloadJson_ValidObject_Passes()
    {
        // Arrange: minimal JSON object with one key/value pair.
        var input = BuildValid(formPayloadJson: "{\"k\":1}");

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.FormPayloadJson);
    }

    [Fact]
    public void AttachmentDocumentIds_EmptyList_Passes()
    {
        // Arrange: empty list — NotNull is satisfied; ForEach trivially passes.
        var input = BuildValid(attachmentDocumentIds: Array.Empty<string>());

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.AttachmentDocumentIds);
    }

    [Fact]
    public void AttachmentDocumentIds_ContainsEmpty_Fails()
    {
        // Arrange: one valid id + one empty id — empty id triggers child NotEmpty failure.
        var input = BuildValid(attachmentDocumentIds: AttachmentsWithEmpty);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor("AttachmentDocumentIds[1]");
    }

    [Fact]
    public void AttachmentDocumentIds_ContainsTooShort_Fails()
    {
        // Arrange: a 3-character id — fails the child Length(4,64) rule.
        var input = BuildValid(attachmentDocumentIds: AttachmentsTooShort);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldHaveValidationErrorFor("AttachmentDocumentIds[0]");
    }

    [Fact]
    public void Valid_Input_PassesAllRules()
    {
        // Arrange: every field at a known-good value.
        var input = BuildValid(
            servicePassportId: "ssp1234567",
            formPayloadJson: "{\"name\":\"John\"}",
            attachmentDocumentIds: AttachmentsValid);

        // Act
        var result = _validator.TestValidate(input);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }
}
