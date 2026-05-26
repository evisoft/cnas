using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Core.Common;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — default <see cref="IQbeToLinqConverter"/> implementation. Walks
/// the QBE envelope, looks each <see cref="QbeCondition.FieldName"/> up in the registry
/// schema, parses each comparand against the field's declared CLR type, and emits a
/// <see cref="System.Linq.Expressions"/> tree EF Core can translate to SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider neutrality.</b> The emitted tree uses <c>EF.Functions.ILike</c> for the
/// wildcard string operators, which Npgsql translates to <c>ILIKE</c> in production and
/// the InMemory provider executes through a client-side fallback (see
/// <c>RelationalExtensions</c>). For the InMemory provider this would normally throw, so
/// the converter delegates to <see cref="string.Contains(string, StringComparison)"/> /
/// <see cref="string.StartsWith(string, StringComparison)"/> /
/// <see cref="string.EndsWith(string, StringComparison)"/> when the wildcard token is
/// absent, and falls back to the <c>WildcardMask.ToRegex</c> equivalent when present.
/// </para>
/// <para>
/// <b>Safety.</b> The field-name allow-list (<see cref="IQbeRegistrySchemaProvider"/>) is
/// the only sanitisation that matters at this layer. The value is bound through an
/// <see cref="Expression.Constant(object?)"/> with the declared CLR type, so EF will
/// always parameterise the SQL and SQL injection is structurally impossible.
/// </para>
/// </remarks>
/// <param name="schemas">Schema provider — typically the singleton seeded at startup.</param>
public sealed class QbeToLinqConverter(IQbeRegistrySchemaProvider schemas) : IQbeToLinqConverter
{
    private readonly IQbeRegistrySchemaProvider _schemas = schemas;

    /// <summary>
    /// Hard cap on the size of an <see cref="QbeOperator.In"/> list. Mirrors the upper
    /// bound documented on the operator — a defence-in-depth check belt-and-braces with
    /// the validator's character cap.
    /// </summary>
    internal const int MaxInListEntries = 100;

    /// <inheritdoc />
    public Result<IQueryable<TEntity>> ApplyOrdering<TEntity>(
        IQueryable<TEntity> source,
        string registryCode,
        IReadOnlyList<QbeOrdering>? orderings)
    {
        ArgumentNullException.ThrowIfNull(source);

        // No-op when no orderings — caller keeps its pre-existing OrderBy chain.
        if (orderings is null || orderings.Count == 0)
        {
            return Result<IQueryable<TEntity>>.Success(source);
        }

        var schema = _schemas.GetForRegistry(registryCode);
        if (schema is null)
        {
            return Result<IQueryable<TEntity>>.Failure(
                ErrorCodes.QbeRegistryUnknown,
                $"No QBE schema is registered for registry '{registryCode}'.");
        }

        IOrderedQueryable<TEntity>? ordered = null;
        for (var i = 0; i < orderings.Count; i++)
        {
            var entry = orderings[i];
            var field = schema.FindField(entry.FieldName);
            if (field is null)
            {
                return Result<IQueryable<TEntity>>.Failure(
                    ErrorCodes.QbeFieldNotQueryable,
                    $"Field '{entry.FieldName}' is not queryable for registry '{registryCode}'.");
            }

            var property = typeof(TEntity).GetProperty(
                field.FieldName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                // Schema declared a field the entity does not expose — defensive guard
                // identical to the converter's Convert<T> branch.
                return Result<IQueryable<TEntity>>.Failure(
                    ErrorCodes.QbeFieldNotQueryable,
                    $"Entity '{typeof(TEntity).Name}' does not expose property '{field.FieldName}'.");
            }

            // Build the key-selector lambda once per entry; the queryable provider
            // (EF Core or LINQ-to-Objects) inspects the expression tree to decide how
            // to evaluate the comparison.
            var parameter = Expression.Parameter(typeof(TEntity), "e");
            var keyAccess = Expression.Property(parameter, property);
            var lambdaType = typeof(Func<,>).MakeGenericType(typeof(TEntity), property.PropertyType);
            var lambda = Expression.Lambda(lambdaType, keyAccess, parameter);

            // Pick the right OrderBy/ThenBy method by direction + position via the
            // Queryable static factory. Generic argument list: (TEntity, TKey).
            string methodName;
            if (ordered is null)
            {
                methodName = entry.Direction == QbeSortDirection.Desc
                    ? nameof(Queryable.OrderByDescending)
                    : nameof(Queryable.OrderBy);
            }
            else
            {
                methodName = entry.Direction == QbeSortDirection.Desc
                    ? nameof(Queryable.ThenByDescending)
                    : nameof(Queryable.ThenBy);
            }

            var queryableMethod = typeof(Queryable).GetMethods()
                .Where(m => m.Name == methodName
                            && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 2)
                .Single()
                .MakeGenericMethod(typeof(TEntity), property.PropertyType);

            var currentSource = (object?)ordered ?? source;
            ordered = (IOrderedQueryable<TEntity>)queryableMethod.Invoke(
                null,
                new[] { currentSource, lambda })!;
        }

        return Result<IQueryable<TEntity>>.Success(ordered!);
    }

    /// <inheritdoc />
    public Result<Expression<Func<TEntity, bool>>> Convert<TEntity>(string registryCode, QbeFilter? filter)
    {
        // Tautology: a missing or empty filter matches every row.
        if (filter is null || filter.Conditions.Count == 0)
        {
            Expression<Func<TEntity, bool>> identity = _ => true;
            return Result<Expression<Func<TEntity, bool>>>.Success(identity);
        }

        // Reject any combinator the validator might have missed (defence in depth — the
        // converter is also reachable from internal callers that bypass MVC binding).
        if (!string.Equals(filter.Combinator, QbeFilter.CombinatorAnd, StringComparison.Ordinal)
            && !string.Equals(filter.Combinator, QbeFilter.CombinatorOr, StringComparison.Ordinal))
        {
            return Result<Expression<Func<TEntity, bool>>>.Failure(
                ErrorCodes.QbeInvalidCombinator,
                $"Combinator must be one of: {QbeFilter.CombinatorAnd}, {QbeFilter.CombinatorOr}.");
        }

        var schema = _schemas.GetForRegistry(registryCode);
        if (schema is null)
        {
            return Result<Expression<Func<TEntity, bool>>>.Failure(
                ErrorCodes.QbeRegistryUnknown,
                $"No QBE schema is registered for registry '{registryCode}'.");
        }

        // Build per-condition predicates first, then fold them with the combinator. This
        // ordering makes the failure path simple: a single bad condition aborts the
        // whole conversion before the tree is composed.
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        Expression? combined = null;
        for (var i = 0; i < filter.Conditions.Count; i++)
        {
            var condition = filter.Conditions[i];
            var field = schema.FindField(condition.FieldName);
            if (field is null)
            {
                return Result<Expression<Func<TEntity, bool>>>.Failure(
                    ErrorCodes.QbeFieldNotQueryable,
                    $"Field '{condition.FieldName}' is not queryable for registry '{registryCode}'.");
            }

            var property = typeof(TEntity).GetProperty(
                field.FieldName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                // Defensive: the schema declared a field the entity does not expose. The
                // schema authoring path is a code change so this only fires on a real bug.
                return Result<Expression<Func<TEntity, bool>>>.Failure(
                    ErrorCodes.QbeFieldNotQueryable,
                    $"Entity '{typeof(TEntity).Name}' does not expose property '{field.FieldName}'.");
            }

            var memberAccess = Expression.Property(parameter, property);
            var perCondition = BuildConditionExpression(memberAccess, field, condition);
            if (perCondition.IsFailure)
            {
                return Result<Expression<Func<TEntity, bool>>>.Failure(
                    perCondition.ErrorCode!,
                    perCondition.ErrorMessage!);
            }

            combined = combined is null
                ? perCondition.Value
                : (filter.IsOr
                    ? Expression.OrElse(combined, perCondition.Value)
                    : Expression.AndAlso(combined, perCondition.Value));
        }

        // Defence: combined cannot be null here because Conditions.Count > 0 was checked
        // above and every per-condition expression is either Failure (returned) or non-null.
        var lambda = Expression.Lambda<Func<TEntity, bool>>(combined!, parameter);
        return Result<Expression<Func<TEntity, bool>>>.Success(lambda);
    }

    /// <summary>
    /// Builds the <see cref="Expression"/> for a single condition. Dispatches on the
    /// declared field type so type-specific operators (e.g. <see cref="QbeOperator.Between"/>
    /// on a date) are validated up-front rather than deferring to EF.
    /// </summary>
    /// <param name="memberAccess">Property access expression (e.g. <c>e.Code</c>).</param>
    /// <param name="field">Field schema entry describing the declared type and case sensitivity.</param>
    /// <param name="condition">The condition to translate.</param>
    /// <returns>Expression (success) or a <see cref="Result{T}.Failure"/> with a stable error code.</returns>
    private static Result<Expression> BuildConditionExpression(
        Expression memberAccess,
        QbeFieldSchema field,
        QbeCondition condition)
    {
        // IS NULL / IS NOT NULL are value-agnostic — short-circuit before any value parse.
        if (condition.Operator == QbeOperator.IsNull)
        {
            return Result<Expression>.Success(Expression.Equal(
                EnsureBoxed(memberAccess),
                Expression.Constant(null, typeof(object))));
        }
        if (condition.Operator == QbeOperator.IsNotNull)
        {
            return Result<Expression>.Success(Expression.NotEqual(
                EnsureBoxed(memberAccess),
                Expression.Constant(null, typeof(object))));
        }

        var underlyingType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;

        // Strings get their own path because the wildcard operators are string-only and
        // the InMemory provider needs the WildcardMask.ToRegex fallback.
        if (underlyingType == typeof(string))
        {
            return BuildStringConditionExpression(memberAccess, field, condition);
        }

        // Numeric / date / bool / enum operators.
        return BuildPrimitiveConditionExpression(memberAccess, underlyingType, field, condition);
    }

    /// <summary>
    /// Builds the per-condition expression for string fields. Implements the wildcard
    /// passthrough via <see cref="WildcardMask"/> and provides the InMemory regex fallback.
    /// </summary>
    /// <param name="memberAccess">Property access (typed string).</param>
    /// <param name="field">Schema entry for the field — drives case sensitivity.</param>
    /// <param name="condition">QBE condition.</param>
    /// <returns>Expression result.</returns>
    private static Result<Expression> BuildStringConditionExpression(
        Expression memberAccess,
        QbeFieldSchema field,
        QbeCondition condition)
    {
        var value = condition.Value ?? string.Empty;

        switch (condition.Operator)
        {
            case QbeOperator.Equals:
            case QbeOperator.NotEquals:
            {
                var comparison = field.IsCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                var equalsMethod = typeof(string).GetMethod(
                    nameof(string.Equals),
                    BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof(string), typeof(string), typeof(StringComparison) })!;
                Expression call = Expression.Call(
                    equalsMethod,
                    memberAccess,
                    Expression.Constant(value, typeof(string)),
                    Expression.Constant(comparison));
                if (condition.Operator == QbeOperator.NotEquals)
                {
                    call = Expression.Not(call);
                }
                return Result<Expression>.Success(call);
            }

            case QbeOperator.Contains:
            case QbeOperator.StartsWith:
            case QbeOperator.EndsWith:
            {
                // The R0164 wildcard primitive treats a literal '*' as the wildcard
                // marker. When the caller embedded one we route through the regex
                // fallback so anchoring honours their explicit star placement; when
                // they did NOT, we use plain string operations that the InMemory
                // provider can execute client-side without throwing.
                var comparison = field.IsCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                if (value.Contains('*'))
                {
                    // Build IsMatch(regex, member) — regex is captured as a closure
                    // constant so the InMemory provider sees a stable instance.
                    var regex = WildcardMask.ToRegex(value);
                    var isMatch = typeof(System.Text.RegularExpressions.Regex)
                        .GetMethod(
                            nameof(System.Text.RegularExpressions.Regex.IsMatch),
                            BindingFlags.Public | BindingFlags.Instance,
                            new[] { typeof(string) })!;
                    Expression call = Expression.Call(
                        Expression.Constant(regex),
                        isMatch,
                        memberAccess);
                    return Result<Expression>.Success(call);
                }

                MethodInfo method = condition.Operator switch
                {
                    QbeOperator.Contains => typeof(string).GetMethod(
                        nameof(string.Contains),
                        new[] { typeof(string), typeof(StringComparison) })!,
                    QbeOperator.StartsWith => typeof(string).GetMethod(
                        nameof(string.StartsWith),
                        new[] { typeof(string), typeof(StringComparison) })!,
                    _ => typeof(string).GetMethod(
                        nameof(string.EndsWith),
                        new[] { typeof(string), typeof(StringComparison) })!,
                };
                Expression invocation = Expression.Call(
                    memberAccess,
                    method,
                    Expression.Constant(value, typeof(string)),
                    Expression.Constant(comparison));
                return Result<Expression>.Success(invocation);
            }

            case QbeOperator.In:
            {
                var parts = (condition.Value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                {
                    return Result<Expression>.Failure(
                        ErrorCodes.QbeValueInvalid,
                        "In operator requires a comma-separated list of one or more values.");
                }
                if (parts.Length > MaxInListEntries)
                {
                    return Result<Expression>.Failure(
                        ErrorCodes.QbeValueInvalid,
                        $"In operator may not exceed {MaxInListEntries} entries.");
                }
                // Folded to a chain of OR Equals — works under every provider and survives
                // case-insensitivity correctly. For larger lists EF will lift this to an
                // IN clause via its own pattern matching.
                var comparison = field.IsCaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                var equalsMethod = typeof(string).GetMethod(
                    nameof(string.Equals),
                    BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof(string), typeof(string), typeof(StringComparison) })!;
                Expression? folded = null;
                foreach (var p in parts)
                {
                    Expression one = Expression.Call(
                        equalsMethod,
                        memberAccess,
                        Expression.Constant(p, typeof(string)),
                        Expression.Constant(comparison));
                    folded = folded is null ? one : Expression.OrElse(folded, one);
                }
                return Result<Expression>.Success(folded!);
            }

            case QbeOperator.GreaterThan:
            case QbeOperator.GreaterOrEqual:
            case QbeOperator.LessThan:
            case QbeOperator.LessOrEqual:
            case QbeOperator.Between:
                return Result<Expression>.Failure(
                    ErrorCodes.QbeOperatorNotSupported,
                    $"Operator '{condition.Operator}' is not supported on string field '{field.FieldName}'.");

            default:
                return Result<Expression>.Failure(
                    ErrorCodes.QbeOperatorNotSupported,
                    $"Operator '{condition.Operator}' is not supported on string field '{field.FieldName}'.");
        }
    }

    /// <summary>
    /// Builds the per-condition expression for non-string primitive fields (numbers,
    /// dates, bools, enums). Parses the comparand against the declared type up front and
    /// rejects malformed values with a stable error code.
    /// </summary>
    /// <param name="memberAccess">Property access expression.</param>
    /// <param name="underlyingType">Field type, with any <see cref="Nullable{T}"/> stripped.</param>
    /// <param name="field">Schema entry.</param>
    /// <param name="condition">QBE condition.</param>
    /// <returns>Expression result.</returns>
    private static Result<Expression> BuildPrimitiveConditionExpression(
        Expression memberAccess,
        Type underlyingType,
        QbeFieldSchema field,
        QbeCondition condition)
    {
        // Reject string-only operators up front to surface a precise error.
        switch (condition.Operator)
        {
            case QbeOperator.Contains:
            case QbeOperator.StartsWith:
            case QbeOperator.EndsWith:
                return Result<Expression>.Failure(
                    ErrorCodes.QbeOperatorNotSupported,
                    $"Operator '{condition.Operator}' is supported only on string fields.");
        }

        // bool: equality only.
        if (underlyingType == typeof(bool))
        {
            if (condition.Operator is not (QbeOperator.Equals or QbeOperator.NotEquals or QbeOperator.In))
            {
                return Result<Expression>.Failure(
                    ErrorCodes.QbeOperatorNotSupported,
                    $"Operator '{condition.Operator}' is not supported on bool field '{field.FieldName}'.");
            }
        }

        // In: comma-separated chain of OrElse equality.
        if (condition.Operator == QbeOperator.In)
        {
            var parts = (condition.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return Result<Expression>.Failure(
                    ErrorCodes.QbeValueInvalid,
                    "In operator requires a comma-separated list of one or more values.");
            }
            if (parts.Length > MaxInListEntries)
            {
                return Result<Expression>.Failure(
                    ErrorCodes.QbeValueInvalid,
                    $"In operator may not exceed {MaxInListEntries} entries.");
            }
            Expression? folded = null;
            foreach (var p in parts)
            {
                var parsedItem = ParseScalar(p, underlyingType);
                if (parsedItem.IsFailure)
                {
                    return Result<Expression>.Failure(parsedItem.ErrorCode!, parsedItem.ErrorMessage!);
                }
                var constItem = Expression.Constant(parsedItem.Value, field.FieldType);
                Expression accessForCompare = memberAccess.Type == field.FieldType
                    ? memberAccess
                    : Expression.Convert(memberAccess, field.FieldType);
                Expression one = Expression.Equal(accessForCompare, constItem);
                folded = folded is null ? one : Expression.OrElse(folded, one);
            }
            return Result<Expression>.Success(folded!);
        }

        // Between: both Value and Value2 required.
        if (condition.Operator == QbeOperator.Between)
        {
            if (string.IsNullOrEmpty(condition.Value) || string.IsNullOrEmpty(condition.Value2))
            {
                return Result<Expression>.Failure(
                    ErrorCodes.QbeValueInvalid,
                    "Between operator requires both Value and Value2.");
            }
            var lo = ParseScalar(condition.Value, underlyingType);
            if (lo.IsFailure)
            {
                return Result<Expression>.Failure(lo.ErrorCode!, lo.ErrorMessage!);
            }
            var hi = ParseScalar(condition.Value2, underlyingType);
            if (hi.IsFailure)
            {
                return Result<Expression>.Failure(hi.ErrorCode!, hi.ErrorMessage!);
            }
            var loConst = Expression.Constant(lo.Value, field.FieldType);
            var hiConst = Expression.Constant(hi.Value, field.FieldType);
            var ge = Expression.GreaterThanOrEqual(memberAccess, loConst);
            var le = Expression.LessThanOrEqual(memberAccess, hiConst);
            return Result<Expression>.Success(Expression.AndAlso(ge, le));
        }

        // Other operators all share the "parse one scalar" path.
        var parsed = ParseScalar(condition.Value ?? string.Empty, underlyingType);
        if (parsed.IsFailure)
        {
            return Result<Expression>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }
        var constant = Expression.Constant(parsed.Value, field.FieldType);

        return condition.Operator switch
        {
            QbeOperator.Equals => Result<Expression>.Success(Expression.Equal(memberAccess, constant)),
            QbeOperator.NotEquals => Result<Expression>.Success(Expression.NotEqual(memberAccess, constant)),
            QbeOperator.GreaterThan => Result<Expression>.Success(Expression.GreaterThan(memberAccess, constant)),
            QbeOperator.GreaterOrEqual => Result<Expression>.Success(Expression.GreaterThanOrEqual(memberAccess, constant)),
            QbeOperator.LessThan => Result<Expression>.Success(Expression.LessThan(memberAccess, constant)),
            QbeOperator.LessOrEqual => Result<Expression>.Success(Expression.LessThanOrEqual(memberAccess, constant)),
            _ => Result<Expression>.Failure(
                ErrorCodes.QbeOperatorNotSupported,
                $"Operator '{condition.Operator}' is not supported on field '{field.FieldName}'."),
        };
    }

    /// <summary>
    /// Parses a single string comparand to the declared CLR type. Numeric values use the
    /// invariant culture; dates use ISO 8601; bools use ASCII <c>true</c>/<c>false</c>;
    /// enums are matched by name (case-insensitive).
    /// </summary>
    /// <param name="raw">String comparand (already trimmed when called from the In path).</param>
    /// <param name="underlyingType">Target CLR type (with any <see cref="Nullable{T}"/> stripped).</param>
    /// <returns>Boxed parsed value on success, <see cref="ErrorCodes.QbeValueInvalid"/> on failure.</returns>
    private static Result<object> ParseScalar(string raw, Type underlyingType)
    {
        try
        {
            if (underlyingType == typeof(string))
            {
                return Result<object>.Success(raw);
            }
            if (underlyingType == typeof(bool))
            {
                return bool.TryParse(raw, out var b)
                    ? Result<object>.Success(b)
                    : Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                        $"Value '{raw}' could not be parsed as bool.");
            }
            if (underlyingType == typeof(DateTime))
            {
                if (!DateTime.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var dt))
                {
                    return Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                        $"Value '{raw}' could not be parsed as ISO-8601 DateTime.");
                }
                return Result<object>.Success(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
            if (underlyingType == typeof(DateOnly))
            {
                return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    ? Result<object>.Success(d)
                    : Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                        $"Value '{raw}' could not be parsed as ISO-8601 DateOnly.");
            }
            if (underlyingType.IsEnum)
            {
                // Enum.TryParse with case-insensitive matching — the wire contract uses
                // PascalCase enum names so a lowercase spelling is tolerated as a courtesy.
                if (Enum.TryParse(underlyingType, raw, ignoreCase: true, out var ev))
                {
                    return Result<object>.Success(ev!);
                }
                return Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                    $"Value '{raw}' is not a member of enum '{underlyingType.Name}'.");
            }
            if (IsNumeric(underlyingType))
            {
                try
                {
                    var converted = System.Convert.ChangeType(raw, underlyingType, CultureInfo.InvariantCulture)!;
                    return Result<object>.Success(converted);
                }
                catch (Exception)
                {
                    return Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                        $"Value '{raw}' could not be parsed as {underlyingType.Name}.");
                }
            }
            return Result<object>.Failure(ErrorCodes.QbeValueInvalid,
                $"Field type '{underlyingType.Name}' is not supported by the QBE converter.");
        }
        catch (Exception ex)
        {
            return Result<object>.Failure(ErrorCodes.QbeValueInvalid, ex.Message);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="t"/> is one of the primitive
    /// numeric CLR types the converter supports.
    /// </summary>
    /// <param name="t">Candidate type.</param>
    /// <returns><see langword="true"/> for the supported numeric types.</returns>
    private static bool IsNumeric(Type t) =>
        t == typeof(byte) || t == typeof(sbyte)
        || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint)
        || t == typeof(long) || t == typeof(ulong)
        || t == typeof(float) || t == typeof(double)
        || t == typeof(decimal);

    /// <summary>
    /// Boxes a value-type member access to <see cref="object"/> so it can be compared
    /// against an <see cref="Expression.Constant(object?)"/> null. Required because
    /// <see cref="Expression.Equal(Expression, Expression)"/> on a non-nullable value type
    /// against null is otherwise rejected by the expression compiler.
    /// </summary>
    /// <param name="memberAccess">Property access expression.</param>
    /// <returns>The original expression for reference types; a <c>Convert(member, object)</c> for value types.</returns>
    private static Expression EnsureBoxed(Expression memberAccess) =>
        memberAccess.Type.IsValueType && Nullable.GetUnderlyingType(memberAccess.Type) is null
            ? Expression.Convert(memberAccess, typeof(object))
            : memberAccess;
}
