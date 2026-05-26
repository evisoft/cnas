using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1710 / TOR INT 002 / Annex 4 — validates
/// <see cref="OfflineBatchSubmissionInputDto"/>. The validator polices the
/// envelope shape only (filename / size / hash regex / op code); deeper
/// per-row parsing happens inside <c>IOfflineBatchRequestParser</c> so a
/// single failure code surfaces and partial parses can be persisted.
/// </summary>
public sealed class OfflineBatchSubmissionInputValidator : AbstractValidator<OfflineBatchSubmissionInputDto>
{
    /// <summary>Minimum permitted request-file size (bytes). Empty uploads are rejected.</summary>
    public const long MinFileSizeBytes = 1;

    /// <summary>Maximum permitted request-file size (10 MB).</summary>
    public const long MaxFileSizeBytes = 10L * 1024 * 1024;

    /// <summary>Maximum permitted filename length.</summary>
    public const int MaxFileNameLength = 256;

    /// <summary>
    /// Allowed-filename regex — letters, digits, dot, hyphen, and underscore,
    /// ending in <c>.csv</c>. Forbids slashes / backslashes so an attacker
    /// cannot use the filename as a path-traversal vector.
    /// </summary>
    public const string FileNameRegex = @"^[A-Za-z0-9._-]+\.csv$";

    /// <summary>Hash regex — 64 lower-case hex characters (SHA-256).</summary>
    public const string HashSha256Regex = "^[0-9a-f]{64}$";

    private static readonly Regex CompiledFileName = new(
        FileNameRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CompiledHash = new(
        HashSha256Regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public OfflineBatchSubmissionInputValidator()
    {
        RuleFor(x => x.ConsumerSubject)
            .NotEmpty().WithMessage("ConsumerSubject is required.")
            .MaximumLength(128).WithMessage("ConsumerSubject must be 128 characters or fewer.");

        RuleFor(x => x.OpCode)
            .NotEmpty().WithMessage("OpCode is required.")
            .Must(IsKnownOpCode)
            .WithMessage("OpCode is not a known Annex-4 batch op.");

        RuleFor(x => x.RequestFileName)
            .NotEmpty().WithMessage("RequestFileName is required.")
            .MaximumLength(MaxFileNameLength)
            .WithMessage($"RequestFileName must be {MaxFileNameLength} characters or fewer.")
            .Must(s => s is not null && CompiledFileName.IsMatch(s))
            .WithMessage("RequestFileName must match the allowed CSV filename pattern.");

        RuleFor(x => x.RequestFileBytes)
            .NotNull().WithMessage("RequestFileBytes is required.")
            .Must(b => b is not null && b.LongLength >= MinFileSizeBytes)
            .WithMessage("RequestFileBytes must be at least 1 byte.")
            .Must(b => b is not null && b.LongLength <= MaxFileSizeBytes)
            .WithMessage($"RequestFileBytes must be at most {MaxFileSizeBytes} bytes.");

        RuleFor(x => x.RequestFileHashSha256)
            .NotEmpty().WithMessage("RequestFileHashSha256 is required.")
            .Must(s => s is not null && CompiledHash.IsMatch(s))
            .WithMessage("RequestFileHashSha256 must be 64 lower-case hex characters.");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="opCode"/> is a known
    /// <see cref="AnnexFourBatchOp"/> enum-name. Case-sensitive — the stable
    /// wire vocabulary is the exact <c>ToString()</c> output.
    /// </summary>
    /// <param name="opCode">Candidate op-code string.</param>
    /// <returns><c>true</c> iff the string parses to an <see cref="AnnexFourBatchOp"/>.</returns>
    public static bool IsKnownOpCode(string? opCode)
        => !string.IsNullOrWhiteSpace(opCode)
           && Enum.TryParse<AnnexFourBatchOp>(opCode, ignoreCase: false, out _);
}

/// <summary>
/// R1710 / TOR INT 002 — validates the cancellation-reason envelope. Reason
/// must be 3..500 characters; consumed by both the cancel endpoint and the
/// admin cancel surface.
/// </summary>
public sealed class OfflineBatchReasonInputValidator : AbstractValidator<OfflineBatchReasonInputDto>
{
    /// <summary>Minimum reason length.</summary>
    public const int MinReasonLength = 3;

    /// <summary>Maximum reason length.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public OfflineBatchReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(MinReasonLength)
            .WithMessage($"Reason must be at least {MinReasonLength} characters.")
            .MaximumLength(MaxReasonLength)
            .WithMessage($"Reason must be at most {MaxReasonLength} characters.");
    }
}

/// <summary>
/// R1710 / TOR INT 002 — validates the submissions list-filter envelope.
/// </summary>
public sealed class OfflineBatchSubmissionFilterValidator : AbstractValidator<OfflineBatchSubmissionFilterDto>
{
    /// <summary>Maximum permitted page size (Take).</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public OfflineBatchSubmissionFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be in [1, {MaxTake}].");
    }
}

/// <summary>
/// R1710 / TOR INT 002 — validates the rows list-filter envelope.
/// </summary>
public sealed class OfflineBatchRowFilterValidator : AbstractValidator<OfflineBatchRowFilterDto>
{
    /// <summary>Maximum permitted page size (Take).</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public OfflineBatchRowFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be in [1, {MaxTake}].");
    }
}
