namespace Cnas.Ps.Core.Common;

/// <summary>
/// Encodes and decodes Sqid identifiers used at all external boundaries of the system.
/// Per CLAUDE.md RULE 3, every <c>Id</c>/<c>*Id</c> field that leaves the system (REST
/// responses, route parameters, webhook payloads, sharable URLs) is a Sqid-encoded
/// string. Internal code keeps using <see cref="long"/> primary keys.
/// </summary>
/// <remarks>
/// Sqids are reversible — they are not a security boundary. Their job is to keep
/// integer primary-key magnitudes (user counts, order volume, growth rate) opaque
/// to third parties. The alphabet, salt, and minimum length are configured once and
/// must remain stable for the life of the system — changing them breaks every
/// previously-published external reference.
/// </remarks>
public interface ISqidService
{
    /// <summary>
    /// Encodes a 64-bit database identifier to an opaque external Sqid string.
    /// </summary>
    /// <param name="id">A non-negative database primary key.</param>
    /// <returns>A Sqid string of at least the configured minimum length.</returns>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="id"/> is negative.</exception>
    string Encode(long id);

    /// <summary>
    /// Decodes a Sqid string back to its 64-bit database identifier.
    /// </summary>
    /// <param name="sqid">The Sqid as it appeared on an inbound API request.</param>
    /// <returns>A successful result holding the decoded id, or a failure with <see cref="ErrorCodes.InvalidSqid"/>.</returns>
    Result<long> TryDecode(string? sqid);
}
