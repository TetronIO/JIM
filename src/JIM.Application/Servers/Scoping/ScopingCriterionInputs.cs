// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The attributes a scoping criterion reads (#2054): the attribute it compares, or each attribute its expression reads
/// on the scoped side. Every place that asks which attributes scoping depends on asks this, so an expression criterion's
/// inputs are never missed by one of them.
/// </summary>
/// <remarks>
/// An expression's inputs are found from its text (<see cref="ExpressionInputResolver"/>) and matched to the schema by
/// name, ignoring case as expressions do. A name the schema does not hold reads nothing; saving refuses such an
/// expression, so one is only met in a rule saved before its attribute was removed.
/// </remarks>
public static class ScopingCriterionInputs
{
    /// <summary>
    /// The Metaverse Attributes an export rule's <paramref name="criterion"/> reads, each once, taken from
    /// <paramref name="schema"/> for an expression.
    /// </summary>
    public static IEnumerable<MetaverseAttribute> MetaverseAttributes(SyncRuleScopingCriteria criterion, IEnumerable<MetaverseAttribute> schema)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (!criterion.IsExpression)
            return criterion.MetaverseAttribute is { } attribute ? [attribute] : [];

        return Named(criterion, ExpressionInputSource.Metaverse, schema, attribute => attribute.Name);
    }

    /// <summary>
    /// The Connected System Object Type attributes an import rule's <paramref name="criterion"/> reads, each once, taken
    /// from <paramref name="schema"/> for an expression.
    /// </summary>
    public static IEnumerable<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes(SyncRuleScopingCriteria criterion,
        IEnumerable<ConnectedSystemObjectTypeAttribute> schema)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        if (!criterion.IsExpression)
            return criterion.ConnectedSystemAttribute is { } attribute ? [attribute] : [];

        return Named(criterion, ExpressionInputSource.ConnectedSystem, schema, attribute => attribute.Name);
    }

    private static IEnumerable<TAttribute> Named<TAttribute>(SyncRuleScopingCriteria criterion, ExpressionInputSource side,
        IEnumerable<TAttribute> schema, Func<TAttribute, string> nameOf)
    {
        var attributes = schema as IReadOnlyCollection<TAttribute> ?? schema.ToList();
        return ExpressionInputResolver.ResolveCached(criterion.Expression)
            .Where(input => input.Source == side)
            .Select(input => attributes.FirstOrDefault(attribute => string.Equals(nameOf(attribute), input.AttributeName, StringComparison.OrdinalIgnoreCase)))
            .Where(attribute => attribute != null)
            .Select(attribute => attribute!)
            .Distinct();
    }
}
