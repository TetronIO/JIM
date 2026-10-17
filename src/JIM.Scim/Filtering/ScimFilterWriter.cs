// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Scim.Schema;

namespace JIM.Scim.Filtering;

/// <summary>
/// Writes a <see cref="ScimFilter"/> or <see cref="ScimPatchPath"/> in canonical form: operators and keywords lower
/// case, single spaces, and parentheses only where the tree needs them, so that <see cref="ScimFilterParser"/> reads
/// back exactly the tree written. String values go through <see cref="ScimFilterLiteral.Quote"/>, so whatever a value
/// holds it stays one value and never becomes grammar.
/// </summary>
public static class ScimFilterWriter
{
    public static string Write(ScimFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var text = new StringBuilder();
        Append(text, filter);
        return text.ToString();
    }

    public static string Write(ScimAttributePath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var text = new StringBuilder();
        Append(text, path);
        return text.ToString();
    }

    public static string WritePath(ScimPatchPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var text = new StringBuilder();
        Append(text, path.Attribute);

        if (path.ValueFilter != null)
        {
            text.Append('[');
            Append(text, path.ValueFilter);
            text.Append(']');
        }

        if (path.SubAttributeName != null)
            text.Append('.').Append(path.SubAttributeName);

        return text.ToString();
    }

    private static void Append(StringBuilder text, ScimFilter filter)
    {
        switch (filter)
        {
            case ScimComparisonFilter comparison:
                // A tree the parser could never have produced would be written as grammar it refuses to read back.
                if ((comparison.Operator == ScimComparisonOperator.Pr) != (comparison.Value == null))
                    throw new ArgumentException("'pr' takes no comparison value, and every other operator takes one.", nameof(filter));

                Append(text, comparison.Path);
                text.Append(' ').Append(comparison.Operator.ToString().ToLowerInvariant());
                if (comparison.Value != null)
                    text.Append(' ').Append(Literal(comparison.Value));
                break;

            case ScimLogicalFilter logical:
                // A run of one operator groups from the left as the parser reads it, so only a right operand of the
                // same operator, or an operand of a looser one, needs parentheses to keep its shape.
                AppendOperand(text, logical.Left, NeedsParentheses(logical.Left, logical.Operator, rightOperand: false));
                text.Append(logical.Operator == ScimLogicalOperator.And ? " and " : " or ");
                AppendOperand(text, logical.Right, NeedsParentheses(logical.Right, logical.Operator, rightOperand: true));
                break;

            case ScimNotFilter not:
                text.Append("not (");
                Append(text, not.Inner);
                text.Append(')');
                break;

            case ScimValuePathFilter valuePath:
                Append(text, valuePath.Path);
                text.Append('[');
                Append(text, valuePath.Filter);
                text.Append(']');
                break;

            default:
                throw new ArgumentException($"Unknown filter node {filter.GetType().Name}.", nameof(filter));
        }
    }

    private static void AppendOperand(StringBuilder text, ScimFilter operand, bool parenthesise)
    {
        if (parenthesise)
            text.Append('(');
        Append(text, operand);
        if (parenthesise)
            text.Append(')');
    }

    private static bool NeedsParentheses(ScimFilter operand, ScimLogicalOperator parent, bool rightOperand)
    {
        if (operand is not ScimLogicalFilter child)
            return false;

        var childBinding = Binding(child.Operator);
        var parentBinding = Binding(parent);
        return rightOperand ? childBinding <= parentBinding : childBinding < parentBinding;
    }

    private static int Binding(ScimLogicalOperator logical) => logical == ScimLogicalOperator.And ? 2 : 1;

    private static void Append(StringBuilder text, ScimAttributePath path)
    {
        if (path.SchemaUrn != null)
            text.Append(path.SchemaUrn).Append(':');

        text.Append(path.AttributeName);

        if (path.SubAttributeName != null)
            text.Append('.').Append(path.SubAttributeName);
    }

    private static string Literal(ScimFilterValue value) => value.Kind switch
    {
        ScimFilterValueKind.String => ScimFilterLiteral.Quote(value.Text!),
        ScimFilterValueKind.Null => "null",
        _ => value.Text!
    };
}
