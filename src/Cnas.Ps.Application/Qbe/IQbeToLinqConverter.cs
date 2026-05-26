using System.Linq.Expressions;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Qbe;

/// <summary>
/// R0163 / TOR UI 009 — converts a <see cref="QbeFilter"/> against a registered
/// <see cref="QbeRegistrySchema"/> into a strongly typed LINQ predicate expression. The
/// predicate is composable — callers chain it onto an existing <see cref="IQueryable{T}"/>
/// pipeline (e.g. <c>query.Where(predicate)</c>) and let EF translate it to SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure modes (all map to <see cref="Result{T}.Failure"/>):</b>
/// <list type="bullet">
///   <item><see cref="ErrorCodes.QbeRegistryUnknown"/> — registry code not registered.</item>
///   <item><see cref="ErrorCodes.QbeFieldNotQueryable"/> — field name not in the schema.</item>
///   <item><see cref="ErrorCodes.QbeOperatorNotSupported"/> — operator incompatible with the field type.</item>
///   <item><see cref="ErrorCodes.QbeValueInvalid"/> — value could not be parsed against the field type.</item>
///   <item><see cref="ErrorCodes.QbeInvalidCombinator"/> — combinator not one of <c>AND</c>/<c>OR</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Wildcards.</b> String operators <see cref="QbeOperator.Contains"/> /
/// <see cref="QbeOperator.StartsWith"/> / <see cref="QbeOperator.EndsWith"/> pull through
/// the R0164 <c>WildcardMask</c> primitive when the value carries an explicit <c>*</c>, so
/// <c>"ESCU*"</c> behaves as a prefix match even when the operator nominally asks for
/// "contains".
/// </para>
/// <para>
/// <b>Empty filter.</b> An empty conditions list short-circuits to a tautology
/// (<c>x =&gt; true</c>) — equivalent to passing a <see langword="null"/> filter. This
/// keeps callers simple: the service layer can pass the filter through unconditionally
/// when the controller binds an empty body.
/// </para>
/// </remarks>
public interface IQbeToLinqConverter
{
    /// <summary>
    /// Builds a predicate expression that the caller can splice onto an
    /// <see cref="IQueryable{TEntity}"/> via <c>Where</c>.
    /// </summary>
    /// <typeparam name="TEntity">The entity type the predicate targets.</typeparam>
    /// <param name="registryCode">
    /// Registry code identifying the schema to apply. Must be registered with the
    /// underlying <see cref="IQbeRegistrySchemaProvider"/>.
    /// </param>
    /// <param name="filter">The QBE filter to convert. <see langword="null"/> is treated as the empty filter.</param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> carrying the predicate expression, or a failure
    /// with one of the stable error codes listed in the type-level remarks.
    /// </returns>
    Result<Expression<Func<TEntity, bool>>> Convert<TEntity>(string registryCode, QbeFilter? filter);

    /// <summary>
    /// R0523 / TOR CF 03.05 — applies a multi-field ordering chain onto the supplied
    /// queryable, returning an <see cref="IQueryable{TEntity}"/> on which the caller can
    /// chain <c>Skip</c>/<c>Take</c>/<c>Select</c>. Empty / <see langword="null"/>
    /// <paramref name="orderings"/> is treated as a no-op success — the caller's
    /// preferred default ordering should already be in place on <paramref name="source"/>.
    /// </summary>
    /// <typeparam name="TEntity">Entity type targeted by the queryable.</typeparam>
    /// <param name="source">Queryable to splice the ordering onto (typically post-Where).</param>
    /// <param name="registryCode">
    /// Registry code identifying the schema. Used to validate every entry in
    /// <paramref name="orderings"/> resolves to a known queryable field.
    /// </param>
    /// <param name="orderings">
    /// Ordered list of ordering entries. The first entry maps to <c>OrderBy</c>; each
    /// subsequent entry maps to <c>ThenBy</c>. A <see cref="QbeSortDirection.Desc"/>
    /// entry maps to the descending counterpart.
    /// </param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> carrying the ordered queryable. Failure
    /// codes mirror the <see cref="Convert{TEntity}"/> contract:
    /// <list type="bullet">
    ///   <item><see cref="ErrorCodes.QbeRegistryUnknown"/> — registry code not registered.</item>
    ///   <item><see cref="ErrorCodes.QbeFieldNotQueryable"/> — entry field not on the schema.</item>
    /// </list>
    /// </returns>
    Result<IQueryable<TEntity>> ApplyOrdering<TEntity>(
        IQueryable<TEntity> source,
        string registryCode,
        IReadOnlyList<QbeOrdering>? orderings);
}
