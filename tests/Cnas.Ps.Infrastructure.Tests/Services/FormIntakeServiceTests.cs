using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="FormIntakeService"/> — UC07 server-side form intake
/// validation. Uses EF Core InMemory for the persistence backend and NSubstitute for
/// <see cref="ISqidService"/>. Asserts both the failure-shape (error code + message
/// substring) and the accumulation behaviour required when a payload violates multiple
/// schema constraints simultaneously.
/// </summary>
public class FormIntakeServiceTests
{
    /// <summary>Deterministic clock used across the suite to keep audit / snapshot fields stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── Argument / boundary failures ───────────────────────

    [Fact]
    public async Task ValidateAsync_EmptyServicePassportId_ReturnsValidationFailed()
    {
        var harness = Harness.Create();

        var result = await harness.Service.ValidateAsync(servicePassportId: "", formPayloadJson: "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ValidateAsync_EmptyFormPayload_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        // Stub Sqid so the service does not bail on InvalidSqid before reaching the payload guard.
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(1L));

        var result = await harness.Service.ValidateAsync("PP", formPayloadJson: "   ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ValidateAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("garbage").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.ValidateAsync("garbage", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    // ─────────────────────── Passport-side failures ───────────────────────

    [Fact]
    public async Task ValidateAsync_PassportNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.ValidateAsync("missing", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ValidateAsync_PassportDisabled_ReturnsNotFound()
    {
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: "{}", isEnabled: false);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── JSON parsing failures ───────────────────────

    [Fact]
    public async Task ValidateAsync_BadFormJson_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: "{}");
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", formPayloadJson: "{not-valid-json");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public async Task ValidateAsync_CorruptSchemaJson_ReturnsInternal()
    {
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: "{this is not schema");
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Internal);
    }

    // ─────────────────────── Schema-driven validation ───────────────────────

    [Fact]
    public async Task ValidateAsync_MissingRequiredField_AccumulatesMessage()
    {
        var schema = """
        {
          "required": ["idnp"],
          "properties": {
            "idnp": { "type": "string" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", formPayloadJson: "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("idnp");
        result.ErrorMessage.Should().Contain("required");
    }

    [Fact]
    public async Task ValidateAsync_WrongType_AccumulatesMessage()
    {
        // 'age' declared as integer; payload supplies a string. Service should surface the
        // type mismatch with the offending field name in the message.
        var schema = """
        {
          "properties": {
            "age": { "type": "integer" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"age":"forty"}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("age");
        result.ErrorMessage.Should().Contain("integer");
    }

    [Fact]
    public async Task ValidateAsync_StringTooShort_AccumulatesMessage()
    {
        var schema = """
        {
          "properties": {
            "name": { "type": "string", "minLength": 3 }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"name":"ab"}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("name");
        result.ErrorMessage.Should().Contain("minLength");
    }

    [Fact]
    public async Task ValidateAsync_StringPatternMismatch_AccumulatesMessage()
    {
        // Pattern matches exactly 13 digits — payload is letters, must fail.
        var schema = """
        {
          "properties": {
            "idnp": { "type": "string", "pattern": "^\\d{13}$" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"idnp":"not-digits"}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("idnp");
        result.ErrorMessage.Should().Contain("pattern");
    }

    [Fact]
    public async Task ValidateAsync_IntegerOutOfRange_AccumulatesMessage()
    {
        var schema = """
        {
          "properties": {
            "age": { "type": "integer", "minimum": 18, "maximum": 120 }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"age":15}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("age");
        result.ErrorMessage.Should().Contain("minimum");
    }

    [Fact]
    public async Task ValidateAsync_BadDateFormat_AccumulatesMessage()
    {
        var schema = """
        {
          "properties": {
            "birthDate": { "type": "date" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"birthDate":"19/05/2026"}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("birthDate");
        result.ErrorMessage.Should().Contain("date");
    }

    [Fact]
    public async Task ValidateAsync_MultipleViolations_BothAppearInOutput()
    {
        // 'age' too low + 'name' too short — both messages must be accumulated, not
        // short-circuited on the first one. This is the contract the spec calls out
        // explicitly so callers can render a complete validation summary in one round-trip.
        var schema = """
        {
          "properties": {
            "age":  { "type": "integer", "minimum": 18 },
            "name": { "type": "string",  "minLength": 5 }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync("PP", """{"age":10,"name":"ab"}""");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("age");
        result.ErrorMessage.Should().Contain("name");
        // Messages should be semicolon-joined per the spec.
        result.ErrorMessage.Should().Contain(";");
    }

    [Fact]
    public async Task ValidateAsync_LenientToUnknownFields_StillSucceeds()
    {
        // No 'required' list, the only known property 'idnp' is present and well-formed.
        // The schema does not mention 'extraField'; per the spec, unknown payload fields are
        // allowed (lenient default), so the result must still be Success.
        var schema = """
        {
          "properties": {
            "idnp": { "type": "string" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var result = await harness.Service.ValidateAsync(
            "PP",
            """{"idnp":"2000000000006","extraField":"ignored"}""");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_HappyPath_ReturnsSuccess()
    {
        var schema = """
        {
          "required": ["idnp", "age"],
          "properties": {
            "idnp":      { "type": "string",  "pattern": "^\\d{13}$" },
            "age":       { "type": "integer", "minimum": 18, "maximum": 120 },
            "birthDate": { "type": "date" }
          }
        }
        """;
        var harness = Harness.Create();
        var passport = await harness.SeedPassportAsync(schemaJson: schema);
        harness.Sqids.TryDecode("PP").Returns(Result<long>.Success(passport.Id));

        var payload = """{"idnp":"2000000000006","age":42,"birthDate":"1984-01-15"}""";
        var result = await harness.Service.ValidateAsync("PP", payload);

        result.IsSuccess.Should().BeTrue();
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-formintake-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required FormIntakeService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        public static Harness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);

            var service = new FormIntakeService(db, sqids, clock);
            return new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Clock = clock,
            };
        }

        /// <summary>Inserts an active+enabled <see cref="ServicePassport"/> with the supplied schema JSON.</summary>
        public async Task<ServicePassport> SeedPassportAsync(
            string schemaJson,
            bool isEnabled = true,
            bool isActive = true)
        {
            var entity = new ServicePassport
            {
                Code = $"SP-{Guid.NewGuid():N}",
                NameRo = "Pașaport test",
                DescriptionRo = "Test",
                WorkflowCode = "wf-test",
                FormSchemaJson = schemaJson,
                IsEnabled = isEnabled,
                IsActive = isActive,
                CreatedAtUtc = ClockNow.AddDays(-1),
            };
            Db.ServicePassports.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
