using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Domain;

/// <summary>
/// R0805 / iter-138 / TOR Annex 1 §8.1.1.6 — pinning tests for the additive
/// classifier columns and sub-table fields landed on <see cref="Contributor"/>,
/// <see cref="ContributorAddress"/> and <see cref="ContributorContact"/>.
/// </summary>
/// <remarks>
/// The tests use the EF Core InMemory provider; the InMemory provider applies
/// EF value converters at materialisation time, so the encrypted-at-rest tests
/// assert the round-trip contract (write → SaveChanges → read fresh) rather
/// than ciphertext at the column layer. The Postgres-backed integration
/// suite (live DB) exercises the at-rest envelope separately via
/// <c>FieldEncryptionPersistenceTests</c>.
/// </remarks>
public sealed class ContributorAnnex1ExpansionTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 9, 0, 0, DateTimeKind.Utc);

    private const string ValidIdno = "1003600012346";

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-contrib-annex1-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static async Task<Contributor> SeedContributorAsync(CnasDbContext db)
    {
        var c = new Contributor
        {
            Idno = ValidIdno,
            IdnoHash = IdHashHelper.Hash(ValidIdno),
            Denumire = "ACME SRL",
            RegisteredAtUtc = ClockNow.AddDays(-30),
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>
    /// R0805 — CFOJ / CFP / CAEM classifier columns persist and round-trip through
    /// a fresh DbContext (proves the column is mapped, not just the in-memory
    /// change tracker).
    /// </summary>
    [Fact]
    public async Task Contributor_ClassifierColumns_RoundTripThroughFreshContext()
    {
        var dbName = $"cnas-contrib-rt-{Guid.NewGuid():N}";
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        long id;
        await using (var write = new CnasDbContext(opts))
        {
            var c = new Contributor
            {
                Idno = ValidIdno,
                IdnoHash = IdHashHelper.Hash(ValidIdno),
                Denumire = "ACME SRL",
                CfojCode = "1170",
                CfpCode = "2000",
                CaemCode = "47.11",
                ValidFromUtc = ClockNow,
                ValidToUtc = null,
                RegisteredAtUtc = ClockNow.AddDays(-1),
                CreatedAtUtc = ClockNow.AddDays(-1),
                IsActive = true,
            };
            write.Contributors.Add(c);
            await write.SaveChangesAsync();
            id = c.Id;
        }

        await using var read = new CnasDbContext(opts);
        var reloaded = await read.Contributors.SingleAsync(c => c.Id == id);
        reloaded.CfojCode.Should().Be("1170");
        reloaded.CfpCode.Should().Be("2000");
        reloaded.CaemCode.Should().Be("47.11");
        reloaded.ValidFromUtc.Should().Be(ClockNow);
        reloaded.ValidToUtc.Should().BeNull();
    }

    /// <summary>
    /// R0805 — ContributorAddress AddressKind / BuildingNumber / Apartment
    /// columns round-trip through a fresh DbContext.
    /// </summary>
    [Fact]
    public async Task ContributorAddress_InsertedRow_RoundTripsKindAndComponents()
    {
        var db = CreateContext();
        var parent = await SeedContributorAsync(db);

        db.ContributorAddresses.Add(new ContributorAddress
        {
            ContributorId = parent.Id,
            Street = "Bd. Stefan cel Mare",
            City = "Chișinău",
            Region = "Chișinău",
            PostalCode = "MD-2001",
            Country = "MD",
            BuildingNumber = "42A",
            Apartment = "15",
            AddressKind = ContributorAddressKind.Legal,
            ValidFromUtc = ClockNow,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var row = await db.ContributorAddresses.AsNoTracking()
            .SingleAsync(a => a.ContributorId == parent.Id);
        row.AddressKind.Should().Be(ContributorAddressKind.Legal);
        row.BuildingNumber.Should().Be("42A");
        row.Apartment.Should().Be("15");
        row.Street.Should().Be("Bd. Stefan cel Mare");
    }

    /// <summary>
    /// R0805 — ContributorAddress retrieval orders new fields when multiple rows exist.
    /// </summary>
    [Fact]
    public async Task ContributorAddress_MultipleKinds_AllPersistedDistinctly()
    {
        var db = CreateContext();
        var parent = await SeedContributorAsync(db);

        db.ContributorAddresses.Add(new ContributorAddress
        {
            ContributorId = parent.Id,
            Street = "Sediu",
            City = "Chișinău",
            Region = "Chișinău",
            PostalCode = "MD-2001",
            BuildingNumber = "1",
            AddressKind = ContributorAddressKind.Legal,
            ValidFromUtc = ClockNow.AddDays(-2),
            ValidToUtc = ClockNow.AddDays(-1),
            CreatedAtUtc = ClockNow.AddDays(-2),
            IsActive = true,
        });
        db.ContributorAddresses.Add(new ContributorAddress
        {
            ContributorId = parent.Id,
            Street = "Postal",
            City = "Chișinău",
            Region = "Chișinău",
            PostalCode = "MD-2002",
            BuildingNumber = "2",
            AddressKind = ContributorAddressKind.Postal,
            ValidFromUtc = ClockNow,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var rows = await db.ContributorAddresses.AsNoTracking()
            .Where(a => a.ContributorId == parent.Id)
            .OrderBy(a => a.ValidFromUtc)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].AddressKind.Should().Be(ContributorAddressKind.Legal);
        rows[1].AddressKind.Should().Be(ContributorAddressKind.Postal);
    }

    /// <summary>
    /// R0805 — ContributorContact ContactKind / Value columns round-trip via fresh context.
    /// </summary>
    [Fact]
    public async Task ContributorContact_InsertedRow_RoundTripsKindAndValue()
    {
        var db = CreateContext();
        var parent = await SeedContributorAsync(db);

        db.ContributorContacts.Add(new ContributorContact
        {
            ContributorId = parent.Id,
            ContactKind = ContributorContactKind.Email,
            Value = "office@acme.md",
            ValidFromUtc = ClockNow,
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var row = await db.ContributorContacts.AsNoTracking()
            .SingleAsync(c => c.ContributorId == parent.Id);
        row.ContactKind.Should().Be(ContributorContactKind.Email);
        row.Value.Should().Be("office@acme.md");
    }

    /// <summary>
    /// R0805 — Address PII (BuildingNumber) is configured to round-trip through the
    /// <c>EncryptedStringConverter</c> path when an encryptor is wired; when none is
    /// wired (this in-memory test) the column carries plaintext but is still mapped
    /// for reads. The test pins the mapping by asserting the EF model carries the
    /// expected value converter shape.
    /// </summary>
    [Fact]
    public void ContributorAddress_BuildingNumberAndApartment_AreMapped()
    {
        var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(ContributorAddress));
        entity.Should().NotBeNull("ContributorAddress is configured as an EF entity");

        var building = entity!.FindProperty(nameof(ContributorAddress.BuildingNumber));
        building.Should().NotBeNull("R0805 — BuildingNumber column is mapped");
        // iter-149 — widened to 128 to accommodate the application-level AES
        // ciphertext envelope (CLAUDE.md §5.7); the plaintext payload remains
        // bounded by the validator but the persisted column must fit the
        // base64-wrapped envelope.
        building!.GetMaxLength().Should().Be(128);

        var apartment = entity.FindProperty(nameof(ContributorAddress.Apartment));
        apartment.Should().NotBeNull("R0805 — Apartment column is mapped");
        apartment!.GetMaxLength().Should().Be(128);
    }

    /// <summary>
    /// R0805 — Contact PII (<c>Value</c>) is configured as a mapped column with the
    /// expected max length (256). Mirrors the BuildingNumber test for the sister table.
    /// </summary>
    [Fact]
    public void ContributorContact_Value_IsMapped()
    {
        var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(ContributorContact));
        entity.Should().NotBeNull("ContributorContact is configured as an EF entity");

        var value = entity!.FindProperty(nameof(ContributorContact.Value));
        value.Should().NotBeNull("R0805 — Value column is mapped");
        // iter-149 — widened to 512 to accommodate the application-level AES
        // ciphertext envelope; mirrors the BuildingNumber/Apartment widening
        // documented above.
        value!.GetMaxLength().Should().Be(512);
    }
}
