using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1710 / TOR INT 002 — validator tests for the offline-batch input
/// envelopes. Exercises one happy path and the boundary cases the
/// validator polices (file extension, byte count, hash regex, reason
/// length).
/// </summary>
public sealed class OfflineBatchInputValidatorTests
{
    private const string ValidSha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static OfflineBatchSubmissionInputDto Sample(
        byte[]? bytes = null,
        string? hash = null,
        string? fileName = null,
        string? opCode = null,
        string? subject = null)
        => new(
            ConsumerSubject: subject ?? "client-rsp",
            OpCode: opCode ?? nameof(AnnexFourBatchOp.GetInsuredPersonStatus),
            RequestFileName: fileName ?? "req.csv",
            RequestFileBytes: bytes ?? new byte[] { 1, 2, 3 },
            RequestFileHashSha256: hash ?? ValidSha256);

    /// <summary>R1710 — happy-path submission envelope passes the validator.</summary>
    [Fact]
    public void Submit_HappyPath_Passes()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var r = v.Validate(Sample());
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1710 — non-CSV filename is rejected by the regex rule.</summary>
    [Fact]
    public void Submit_NonCsvFilename_Fails()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var r = v.Validate(Sample(fileName: "req.txt"));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1710 — empty file (0 bytes) is rejected.</summary>
    [Fact]
    public void Submit_EmptyFile_Fails()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var r = v.Validate(Sample(bytes: Array.Empty<byte>()));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1710 — oversized file (>10 MB) is rejected.</summary>
    [Fact]
    public void Submit_OversizedFile_Fails()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var huge = new byte[OfflineBatchSubmissionInputValidator.MaxFileSizeBytes + 1];
        var r = v.Validate(Sample(bytes: huge));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1710 — malformed hash (wrong length) is rejected.</summary>
    [Fact]
    public void Submit_BadHashFormat_Fails()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var r = v.Validate(Sample(hash: "abc"));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1710 — unknown op-code is rejected.</summary>
    [Fact]
    public void Submit_UnknownOpCode_Fails()
    {
        var v = new OfflineBatchSubmissionInputValidator();
        var r = v.Validate(Sample(opCode: "NotARealOp"));
        r.IsValid.Should().BeFalse();
    }

    /// <summary>R1710 — reason validator accepts a 50-character reason.</summary>
    [Fact]
    public void Reason_HappyPath_Passes()
    {
        var v = new OfflineBatchReasonInputValidator();
        var r = v.Validate(new OfflineBatchReasonInputDto(new string('a', 50)));
        r.IsValid.Should().BeTrue();
    }

    /// <summary>R1710 — reason validator rejects a 2-char input (below min).</summary>
    [Fact]
    public void Reason_TooShort_Fails()
    {
        var v = new OfflineBatchReasonInputValidator();
        var r = v.Validate(new OfflineBatchReasonInputDto("ab"));
        r.IsValid.Should().BeFalse();
    }
}
