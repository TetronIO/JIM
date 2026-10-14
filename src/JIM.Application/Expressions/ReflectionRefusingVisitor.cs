// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq.Expressions;
using System.Reflection;
using DynamicExpresso.Exceptions;

namespace JIM.Application.Expressions;

/// <summary>
/// Refuses, when an expression is parsed, any part of it whose type is reflection's own: a <see cref="Type"/>, a
/// <see cref="MemberInfo"/>, an <see cref="Assembly"/> or a <see cref="Module"/>. That takes in <c>GetType()</c> and
/// <c>typeof(...)</c> themselves, so no expression can hold a reflection object to begin with (#2053).
/// </summary>
/// <remarks>
/// DynamicExpresso's own guard refuses a member chained onto a receiver typed <see cref="Type"/>, and its late binder
/// refuses one at run time; this closes the remaining route at save time, where a <see cref="Type"/> re-typed as
/// <c>object</c> (<c>IIF(true, x.GetType(), null).Assembly</c>, or a cast) reached the run-time check only. Nothing an
/// attribute expression legitimately does needs a reflection object. See <c>engineering/EXPRESSION_SECURITY.md</c>,
/// "Late binding".
/// </remarks>
internal sealed class ReflectionRefusingVisitor : ExpressionVisitor
{
    public override Expression? Visit(Expression? node)
    {
        if (node != null && IsReflectionType(node.Type))
            throw new ReflectionNotAllowedException();

        return base.Visit(node);
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> is a reflection object.
    /// </summary>
    internal static bool IsReflectionType(Type type) =>
        typeof(Type).IsAssignableFrom(type) ||
        typeof(MemberInfo).IsAssignableFrom(type) ||
        typeof(Assembly).IsAssignableFrom(type) ||
        typeof(Module).IsAssignableFrom(type);
}
