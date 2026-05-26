using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Audit;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Persistence.Interceptors;

/// <summary>
/// R0184 / TOR SEC 042 — EF Core <see cref="ISaveChangesInterceptor"/> that
/// emits audit rows for every Added / Modified / Deleted entity that carries
/// the <see cref="AutoAuditAttribute"/> marker. Per-property diffs are
/// captured (Modified only) while sensitive fields are masked or excluded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why intercept SaveChanges.</b> Service-layer code already calls
/// <see cref="IAuditService.RecordAsync"/> explicitly for the lifecycle
/// transitions it knows about. The interceptor is the "universal hook" that
/// catches everything else — direct repository writes, ad-hoc admin scripts,
/// background-job mutations — so the audit trail is complete even when a
/// service forgot the explicit call. The two paths are complementary; they
/// do NOT replace each other.
/// </para>
/// <para>
/// <b>Opt-in via attribute.</b> Without the <see cref="AutoAuditAttribute"/>,
/// the interceptor skips the entity. ALL business rows derive from
/// <see cref="AuditableEntity"/>, so we cannot use the base class as the
/// trigger without exploding the audit volume (UserGroups, WorkflowTasks,
/// PersonalAccountEntries would each emit a row on every save).
/// </para>
/// <para>
/// <b>PII discipline (CLAUDE.md §5.6 / SEC 044).</b> The diff JSON respects
/// (a) the <see cref="NotAuditedAttribute"/> on individual properties and
/// (b) a hardcoded backstop list of property NAMES (
/// <see cref="ExcludedPropertyNames"/>) so even un-annotated entities don't
/// leak hash/password/IDNP/IDNO/IBAN fields. Values are also size-capped to
/// 4096 characters per audit row to keep payloads bounded.
/// </para>
/// <para>
/// <b>Scope.</b> Registered Scoped (per-request) — same lifetime as the
/// <see cref="IAuditService"/> it depends on. Registered onto the
/// <c>DbContextOptions</c> via
/// <c>optionsBuilder.AddInterceptors(sp.GetRequiredService&lt;AuditingInterceptor&gt;())</c>
/// in <see cref="Cnas.Ps.Infrastructure.Persistence.DataPersistenceServiceCollectionExtensions"/>.
/// </para>
/// <para>
/// <b>Two-phase capture.</b> Audit payloads are computed in
/// <c>SavingChangesAsync</c> (BEFORE the database write) because EF clears the
/// modified/added/deleted flags after the save and the original values of
/// modified properties are no longer addressable. Emission of the audit row
/// happens AFTER the write in <c>SavedChangesAsync</c> so we don't emit audit
/// rows for transactions that ultimately rolled back. State captured in
/// pending entries is held on the interceptor instance until SavedChanges
/// fires — safe because the interceptor is Scoped (single request).
/// </para>
/// </remarks>
public sealed class AuditingInterceptor : SaveChangesInterceptor
{
    /// <summary>Maximum number of characters retained in any diff/snapshot JSON payload.</summary>
    public const int MaxPayloadChars = 4096;

    /// <summary>
    /// Property names that are NEVER included in audit payloads, regardless of
    /// the <see cref="NotAuditedAttribute"/>. These cover (a) password / hash /
    /// token columns, (b) plaintext PII columns wrapped by
    /// <c>EncryptedStringConverter</c>, and (c) the deterministic-hash shadow
    /// columns. Names are matched case-insensitively. Keep this list small,
    /// documented, and conservative.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the LAST-LINE defence: the per-property
    /// <see cref="NotAuditedAttribute"/> is the canonical excluder. The
    /// hardcoded set ensures that even if a developer adds a new IDNP-shaped
    /// column on an <see cref="AutoAuditAttribute"/>-marked entity, the
    /// interceptor will mask it by name.
    /// </para>
    /// </remarks>
    public static readonly HashSet<string> ExcludedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Password / hash material
        "LocalPasswordHash",
        "AccessTokenHash",
        "RefreshTokenHash",
        "TokenHash",
        "ResetTokenHash",
        "VerificationTokenHash",
        "ApiKeyHash",
        // Plaintext PII (wrapped by EncryptedStringConverter at rest)
        "NationalId",
        "Idnp",
        "Idno",
        "PhoneE164",
        "Email",
        "BankIban",
        "Iban",
        "DebtorIdnp",
        "CreditorAccountIban",
        "BeneficiaryIdnp",
        "LiquidatedDebtorIdno",
        "RecipientCode",
        // Network identifiers (forensic value but treat as PII per SEC 044)
        "ClientIpAddress",
        "IpAddress",
        // Deterministic-hash shadow columns — present alongside their plaintext counterparts
        "NationalIdHash",
        "IdnpHash",
        "IdnoHash",
        "IbanHash",
        "EmailHash",
        "DebtorIdnpHash",
        "CreditorAccountIbanHash",
        "BeneficiaryIdnpHash",
        "LiquidatedDebtorIdnoHash",
        "RecipientCodeHash",
    };

    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Cache of per-CLR-type <see cref="AutoAuditAttribute"/> lookups.</summary>
    private static readonly Dictionary<Type, AutoAuditAttribute?> AttributeCache = new();
    private static readonly object AttributeCacheLock = new();

    /// <summary>Cache of per-CLR-type property name → has-NotAudited-attribute lookups.</summary>
    private static readonly Dictionary<Type, HashSet<string>> NotAuditedCache = new();
    private static readonly object NotAuditedCacheLock = new();

    private readonly IAuditService? _audit;
    private readonly ICallerContext? _caller;
    private readonly ILogger<AuditingInterceptor> _logger;

    /// <summary>
    /// Per-SaveChanges pending captures. Built in <c>SavingChangesAsync</c>
    /// (when entries still carry their original values) and drained in
    /// <c>SavedChangesAsync</c> (so we only emit rows for committed
    /// transactions).
    /// </summary>
    /// <remarks>
    /// EF Core forbids concurrent operations on the same <c>DbContext</c>, so
    /// in well-behaved callers this queue only sees single-threaded access.
    /// We use <see cref="ConcurrentQueue{T}"/> anyway because the Scoped
    /// lifetime means an EF-abuse callsite (e.g. <c>Task.WhenAll(saveTasks)</c>
    /// against a shared context) would otherwise corrupt the list backing
    /// store with race-window adds/clears.
    /// </remarks>
    private readonly ConcurrentQueue<PendingAuditCapture> _pending = new();

    /// <summary>
    /// Set to <c>1</c> while a save is in flight (<c>SavingChangesAsync</c>
    /// → <c>SavedChangesAsync</c> / <c>SaveChangesFailed*</c>). Trips an
    /// <see cref="InvalidOperationException"/> when a second concurrent
    /// SavingChanges fires before the first drains — that signals EF Core
    /// abuse (parallel saves against the same context) and the integrity
    /// invariant of pairing per-save captures with per-save flushes would
    /// otherwise silently break.
    /// </summary>
    private int _saveInFlight;

    /// <summary>Constructs the interceptor with its scoped collaborators.</summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="audit">
    /// Audit-journal façade. Optional — when the host hasn't wired the audit
    /// pipeline (e.g. minimal-DI test scopes that exercise mTLS wiring without
    /// the application layer) the interceptor degrades to a no-op so DI
    /// resolution doesn't trip on missing dependencies.
    /// </param>
    /// <param name="caller">
    /// Caller-context for actor / source-ip / correlation-id attribution.
    /// Optional — same rationale as <paramref name="audit"/>. When null the
    /// emitted audit rows carry <c>"system"</c> as the actor and null
    /// source-ip / correlation-id.
    /// </param>
    public AuditingInterceptor(
        ILogger<AuditingInterceptor> logger,
        IAuditService? audit = null,
        ICallerContext? caller = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _audit = audit;
        _caller = caller;
        _logger = logger;
    }

    /// <summary>Resolves the <see cref="AutoAuditAttribute"/> for a CLR type, cached.</summary>
    /// <param name="clrType">Candidate entity CLR type.</param>
    /// <returns>The attribute when present; <c>null</c> otherwise.</returns>
    internal static AutoAuditAttribute? GetAutoAudit(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (AttributeCacheLock)
        {
            if (AttributeCache.TryGetValue(clrType, out var cached))
            {
                return cached;
            }
            var attr = clrType.GetCustomAttribute<AutoAuditAttribute>(inherit: true);
            AttributeCache[clrType] = attr;
            return attr;
        }
    }

    /// <summary>
    /// Returns the set of property names on <paramref name="clrType"/> marked
    /// with <see cref="NotAuditedAttribute"/>, cached.
    /// </summary>
    /// <param name="clrType">Entity CLR type.</param>
    /// <returns>Names of properties to exclude from the diff JSON.</returns>
    internal static HashSet<string> GetNotAuditedProperties(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (NotAuditedCacheLock)
        {
            if (NotAuditedCache.TryGetValue(clrType, out var cached))
            {
                return cached;
            }
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in clrType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (prop.GetCustomAttribute<NotAuditedAttribute>(inherit: true) is not null)
                {
                    set.Add(prop.Name);
                }
            }
            NotAuditedCache[clrType] = set;
            return set;
        }
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        // Defensive guard against EF abuse — the interceptor expects one
        // SaveChanges at a time per Scoped instance. A concurrent SaveChanges
        // (e.g. Task.WhenAll over the same DbContext) would interleave
        // captures across saves and lose the audit-per-transaction invariant.
        // Atomically claim the in-flight slot; if it was already taken,
        // surface a clear diagnostic rather than corrupting state silently.
        if (Interlocked.CompareExchange(ref _saveInFlight, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "AuditingInterceptor observed a concurrent SaveChanges on the same DbContext scope — EF Core does not support parallel saves. Audit captures from the prior save have not been drained.");
        }
        if (eventData.Context is not null)
        {
            CapturePending(eventData.Context);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        try
        {
            if (_pending.IsEmpty)
            {
                return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
            }

            // Drain by dequeuing into a local list so concurrent SavedChanges
            // fires (rare but possible inside the same scope) don't double-emit.
            var batch = new List<PendingAuditCapture>(_pending.Count);
            while (_pending.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            // No audit-service wired (minimal-DI test scopes) — bump the metric so
            // operators still see the activity, but don't try to record. Production
            // hosts always have IAuditService registered through AddCnasInfrastructure.
            if (_audit is null)
            {
                foreach (var capture in batch)
                {
                    CnasMeter.AuditInterceptorEventEmitted.Add(
                        1, new KeyValuePair<string, object?>("event_code", capture.EventCode));
                }
                return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
            }

            var actor = _caller?.UserSqid ?? "system";
            var sourceIp = _caller?.SourceIp;
            var correlationId = _caller?.CorrelationId;
            foreach (var capture in batch)
            {
                CnasMeter.AuditInterceptorEventEmitted.Add(
                    1, new KeyValuePair<string, object?>("event_code", capture.EventCode));
                try
                {
                    await _audit.RecordAsync(
                        eventCode: capture.EventCode,
                        severity: capture.Severity,
                        actorId: actor,
                        targetEntity: capture.EntityName,
                        targetEntityId: capture.EntityId,
                        detailsJson: capture.DetailsJson,
                        sourceIp: sourceIp,
                        correlationId: correlationId,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Auto-audit MUST NOT fail the request — log loudly and continue.
                    _logger.LogError(ex,
                        "AuditingInterceptor failed to record event {EventCode} for {Entity}/{EntityId}.",
                        capture.EventCode, capture.EntityName, capture.EntityId);
                }
            }

            return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release the save-in-flight slot so the next SaveChanges on this
            // Scoped interceptor can claim it cleanly.
            Interlocked.Exchange(ref _saveInFlight, 0);
        }
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // Transaction did not commit — drain the pending captures so we don't
        // emit phantom rows for rolled-back work.
        DrainPending();
        Interlocked.Exchange(ref _saveInFlight, 0);
        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        DrainPending();
        Interlocked.Exchange(ref _saveInFlight, 0);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Discards any pending captures. Used by the SaveChangesFailed callbacks
    /// so a rolled-back transaction does not leak phantom audit rows.
    /// </summary>
    private void DrainPending()
    {
        while (_pending.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Walks the change-tracker, picks out entries that carry
    /// <see cref="AutoAuditAttribute"/>, and builds the pending capture list
    /// for later emission in <c>SavedChangesAsync</c>.
    /// </summary>
    /// <param name="context">The DbContext being saved.</param>
    private void CapturePending(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var clrType = entry.Entity.GetType();
            var auto = GetAutoAudit(clrType);
            if (auto is null)
            {
                continue;
            }

            var prefix = auto.EventCodePrefix ?? clrType.Name.ToUpperInvariant();
            var stateLabel = entry.State switch
            {
                EntityState.Added => "CREATED",
                EntityState.Modified => "MODIFIED",
                EntityState.Deleted => "DELETED",
                _ => "UNKNOWN",
            };
            var eventCode = $"{prefix}.{stateLabel}";

            var entityId = ExtractEntityId(entry);
            var notAuditedProps = GetNotAuditedProperties(clrType);
            var details = BuildDetails(entry, notAuditedProps);

            _pending.Enqueue(new PendingAuditCapture(
                EventCode: eventCode,
                Severity: auto.Severity,
                EntityName: clrType.Name,
                EntityId: entityId,
                DetailsJson: details));
        }
    }

    /// <summary>
    /// Builds the truncated JSON payload describing the change. Modified ⇒
    /// per-property <c>{old, new}</c> diff. Added ⇒ snapshot of non-excluded
    /// properties. Deleted ⇒ snapshot of original values for non-excluded
    /// properties.
    /// </summary>
    /// <param name="entry">The change-tracker entry.</param>
    /// <param name="notAuditedProps">Set of property names carrying <see cref="NotAuditedAttribute"/>.</param>
    /// <returns>JSON payload, truncated to <see cref="MaxPayloadChars"/>.</returns>
    internal static string BuildDetails(EntityEntry entry, HashSet<string> notAuditedProps)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(notAuditedProps);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["state"] = entry.State.ToString(),
        };

        switch (entry.State)
        {
            case EntityState.Modified:
                var diff = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in entry.Properties)
                {
                    if (!prop.IsModified)
                    {
                        continue;
                    }
                    if (IsExcluded(prop.Metadata.Name, notAuditedProps))
                    {
                        continue;
                    }
                    diff[prop.Metadata.Name] = new
                    {
                        old = ToJsonSafe(prop.OriginalValue),
                        @new = ToJsonSafe(prop.CurrentValue),
                    };
                }
                payload["diff"] = diff;
                break;
            case EntityState.Added:
                var added = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in entry.Properties)
                {
                    if (IsExcluded(prop.Metadata.Name, notAuditedProps))
                    {
                        continue;
                    }
                    added[prop.Metadata.Name] = ToJsonSafe(prop.CurrentValue);
                }
                payload["snapshot"] = added;
                break;
            case EntityState.Deleted:
                var deleted = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in entry.Properties)
                {
                    if (IsExcluded(prop.Metadata.Name, notAuditedProps))
                    {
                        continue;
                    }
                    deleted[prop.Metadata.Name] = ToJsonSafe(prop.OriginalValue);
                }
                payload["snapshot"] = deleted;
                break;
            default:
                break;
        }

        var json = JsonSerializer.Serialize(payload, CachedJsonOptions);
        return json.Length <= MaxPayloadChars ? json : json[..MaxPayloadChars];
    }

    /// <summary>
    /// Returns <c>true</c> when the property must NOT appear in audit
    /// payloads — either carrying <see cref="NotAuditedAttribute"/> or named
    /// in the hardcoded <see cref="ExcludedPropertyNames"/> backstop.
    /// </summary>
    /// <param name="propertyName">Property name.</param>
    /// <param name="notAuditedProps">Per-type <see cref="NotAuditedAttribute"/> set.</param>
    /// <returns><c>true</c> iff the property is excluded.</returns>
    internal static bool IsExcluded(string propertyName, HashSet<string> notAuditedProps)
        => ExcludedPropertyNames.Contains(propertyName) || notAuditedProps.Contains(propertyName);

    /// <summary>
    /// Normalises a property value for JSON serialisation. Byte arrays are
    /// hex-encoded (length-bounded) so they don't break <see cref="JsonSerializer"/>;
    /// everything else is passed through.
    /// </summary>
    /// <param name="value">Raw EF Core property value.</param>
    /// <returns>JSON-friendly representation.</returns>
    internal static object? ToJsonSafe(object? value) => value switch
    {
        null => null,
        byte[] bytes => $"bytes[{bytes.Length}]",
        _ => value,
    };

    /// <summary>
    /// Extracts the surrogate primary-key value for the entry. Returns
    /// <c>null</c> when the entry doesn't expose a long-shaped key.
    /// </summary>
    /// <param name="entry">Change-tracker entry.</param>
    /// <returns>The long-shaped id when available; <c>null</c> otherwise.</returns>
    internal static long? ExtractEntityId(EntityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
        {
            return null;
        }
        if (key.Properties.Count != 1)
        {
            return null;
        }
        var prop = key.Properties[0];
        var raw = entry.Property(prop.Name).CurrentValue;
        return raw switch
        {
            long l => l,
            int i => i,
            short s => s,
            _ => null,
        };
    }

    /// <summary>
    /// A capture computed at <c>SavingChangesAsync</c> and replayed in
    /// <c>SavedChangesAsync</c> — packages everything needed to emit an
    /// <see cref="IAuditService.RecordAsync"/> call.
    /// </summary>
    /// <param name="EventCode">Composed event code (e.g. <c>USERPROFILE.MODIFIED</c>).</param>
    /// <param name="Severity">Severity inherited from <see cref="AutoAuditAttribute.Severity"/>.</param>
    /// <param name="EntityName">CLR type name of the entity.</param>
    /// <param name="EntityId">Surrogate id (<c>long</c>-shaped) or <c>null</c>.</param>
    /// <param name="DetailsJson">Truncated diff / snapshot payload.</param>
    private sealed record PendingAuditCapture(
        string EventCode,
        AuditSeverity Severity,
        string EntityName,
        long? EntityId,
        string DetailsJson);
}
