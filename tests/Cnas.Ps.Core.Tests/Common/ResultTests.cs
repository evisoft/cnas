using Cnas.Ps.Core.Common;
using FluentAssertions;

namespace Cnas.Ps.Core.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Success_WithValue_ExposesValueAndIsSuccess()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failure_WithCodeAndMessage_ExposesBothAndIsFailure()
    {
        var result = Result<int>.Failure(ErrorCodes.NotFound, "missing");

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        result.ErrorMessage.Should().Be("missing");
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        var result = Result<int>.Failure(ErrorCodes.NotFound, "missing");

        var act = () => _ = result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NonGenericSuccess_ExposesSuccess()
    {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void NonGenericFailure_ExposesCode()
    {
        var result = Result.Failure(ErrorCodes.Conflict, "concurrency");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public void From_NonGenericFailure_LiftsToGenericFailure()
    {
        var inner = Result.Failure(ErrorCodes.Forbidden, "no");

        var lifted = Result<string>.From(inner);

        lifted.IsFailure.Should().BeTrue();
        lifted.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        lifted.ErrorMessage.Should().Be("no");
    }
}
