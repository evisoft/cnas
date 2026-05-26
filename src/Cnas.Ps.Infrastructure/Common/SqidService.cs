using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Options;
using Sqids;

namespace Cnas.Ps.Infrastructure.Common;

/// <summary>
/// Default Sqid encoder/decoder implementation backed by the
/// <see href="https://sqids.org">official .NET Sqids library</see>.
/// Singleton, thread-safe.
/// </summary>
public sealed class SqidService : ISqidService
{
    private readonly SqidsEncoder<long> _encoder;

    /// <summary>
    /// Creates the service using configuration from <see cref="SqidOptions"/>.
    /// </summary>
    /// <param name="options">Bound <see cref="SqidOptions"/>.</param>
    public SqidService(IOptions<SqidOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var opts = options.Value;
        _encoder = new SqidsEncoder<long>(new SqidsOptions
        {
            Alphabet = opts.Alphabet,
            MinLength = opts.MinLength,
        });
    }

    /// <inheritdoc />
    public string Encode(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);

        return _encoder.Encode(id);
    }

    /// <inheritdoc />
    public Result<long> TryDecode(string? sqid)
    {
        if (string.IsNullOrWhiteSpace(sqid))
        {
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "Identifier was empty.");
        }

        // The Sqids encoder is permissive about character set but can still throw
        // when the input overflows the long range (OverflowException), contains
        // characters outside the configured alphabet (ArgumentException), or is
        // otherwise unparseable (FormatException). The service contract is to
        // surface a Result.Failure for ANY malformed input — never to let a
        // raw exception escape to the caller — so we trap the documented set
        // and translate to the stable InvalidSqid error code. Letting these
        // exceptions propagate would bypass the Result<T> contract and risk a
        // 500 ProblemDetails being returned for client-supplied junk input.
        IReadOnlyList<long> decoded;
        try
        {
            decoded = _encoder.Decode(sqid);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException)
        {
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "Identifier failed to decode.");
        }

        if (decoded.Count != 1)
        {
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "Identifier failed to decode.");
        }

        return Result<long>.Success(decoded[0]);
    }
}
