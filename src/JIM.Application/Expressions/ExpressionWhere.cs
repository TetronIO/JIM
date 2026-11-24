// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;
using JIM.Application.Resources;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Search;
using JIM.Utilities;

namespace JIM.Application.Expressions;

/// <summary>
/// The expression function <c>Where(values, operator, value)</c> (#2053): keeps the values that meet one of the
/// operator catalogue's operators, compared by the same <see cref="CriterionComparer"/> a scoping criterion uses, so
/// "starts with" means one thing in a scope and in an expression. The operator is named as the editors show it
/// ("starts with") or as the REST API and PowerShell name it (<c>StartsWith</c>).
/// </summary>
/// <remarks>
/// Text compares exactly, like every other expression function, unless <c>ignoreCase</c> is true. Patterns use the
/// catalogue's cached, time-limited regular expressions. The presence operators test an attribute, not a value, so
/// they are refused here.
/// </remarks>
internal static class ExpressionWhere
{
    internal static object? Filter(object? input, object? operatorName, object? value, bool ignoreCase)
    {
        var values = ExpressionValueSets.Values(input);
        if (values.Count == 0)
            return null;

        var type = TypeOf(values[0]!);
        var op = ResolveOperator(operatorName as string, type);
        var expected = ExpectedValue(value, type, op);
        var kept = values.Where(candidate => Meets(candidate!, type, op, expected, !ignoreCase)).ToList();

        return ExpressionValueSets.Collapse(kept, (input as ExpressionValues)?.SourceName);
    }

    private static bool Meets(object candidate, AttributeDataType type, SearchComparisonType op, ScopingValue expected, bool caseSensitive)
    {
        if (TypeOf(candidate) != type)
            throw new ExpressionEvaluationException(ExpressionMessage.Of(nameof(ExpressionMessages.WhereNeedsOneType)));

        var cursor = new OneValueCursor(ToScopingValue(candidate));
        return CriterionComparer.Evaluate(ref cursor, type, op, expected, caseSensitive).Met;
    }

    private static SearchComparisonType ResolveOperator(string? name, AttributeDataType type)
    {
        var valid = SearchComparisonOperators.ValidOperatorsFor(type, CriterionContext.Scoping);
        var trimmed = name?.Trim();
        var match = SearchComparisonOperators.All.Cast<SearchComparisonType?>().FirstOrDefault(op =>
            string.Equals(SearchComparisonOperators.LabelFor(op!.Value, type), trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(op.Value.ToString(), trimmed, StringComparison.OrdinalIgnoreCase));

        if (match is { } op && !SearchComparisonOperators.TakesValue(op))
            throw new ExpressionEvaluationException(
                ExpressionMessage.Of(nameof(ExpressionMessages.WhereOperatorTestsAttribute), trimmed ?? string.Empty));

        if (match is { } found && valid.Contains(found))
            return found;

        var names = string.Join(", ", valid.Where(SearchComparisonOperators.TakesValue).Select(o => $"'{SearchComparisonOperators.LabelFor(o, type)}'"));
        throw new ExpressionEvaluationException(
            ExpressionMessage.Of(nameof(ExpressionMessages.WhereUnknownOperator), trimmed ?? string.Empty, Describe(type), names));
    }

    private static ScopingValue ExpectedValue(object? value, AttributeDataType type, SearchComparisonType op)
    {
        // A value already of the values' type is used as it is; text is read as that type, exactly as an Example Data
        // dependency's value is.
        var one = ExpressionValueSets.One(value);
        if (one is not null and not string && TypeOf(one) == type)
            return ToScopingValue(one);

        var text = one as string ?? (ExpressionText.TryFormat(one, out var formatted) ? formatted : null);
        if (string.IsNullOrEmpty(text))
            throw new ExpressionEvaluationException(ExpressionMessage.Of(nameof(ExpressionMessages.WhereNeedsValue)));

        if (SearchComparisonOperators.IsPattern(op) && SearchComparisonOperators.ValidatePattern(text) is { } patternError)
            throw new ExpressionEvaluationException(patternError);

        var criterion = new CriterionValues();
        if (CriterionValueText.Read(text, type, criterion) is { } readError)
            throw new ExpressionEvaluationException(ExpressionMessage.Of(nameof(ExpressionMessages.WhereCouldNotCompare), readError));

        return new ScopingValue(criterion.StringValue, criterion.IntValue, criterion.LongValue, criterion.DecimalValue,
            criterion.DateTimeValue, criterion.BoolValue, criterion.GuidValue);
    }

    private static AttributeDataType TypeOf(object value) => value switch
    {
        string => AttributeDataType.Text,
        int or short or byte => AttributeDataType.Number,
        long => AttributeDataType.LongNumber,
        decimal => AttributeDataType.Decimal,
        DateTime or DateTimeOffset => AttributeDataType.DateTime,
        bool => AttributeDataType.Boolean,
        Guid => AttributeDataType.Guid,
        _ => throw new ExpressionEvaluationException(ExpressionMessage.Of(nameof(ExpressionMessages.WhereUnsupportedType)))
    };

    private static ScopingValue ToScopingValue(object value) => value switch
    {
        string text => new ScopingValue(text, null, null, null, null, null, null),
        int or short or byte => new ScopingValue(null, Convert.ToInt32(value), null, null, null, null, null),
        long number => new ScopingValue(null, null, number, null, null, null, null),
        decimal number => new ScopingValue(null, null, null, number, null, null, null),
        DateTime or DateTimeOffset when ExpressionDates.TryRead(value, out var date) => new ScopingValue(null, null, null, null, date, null, null),
        bool flag => new ScopingValue(null, null, null, null, null, flag, null),
        Guid guid => new ScopingValue(null, null, null, null, null, null, guid),
        _ => default
    };

    private static JimMessage Describe(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindText)),
        AttributeDataType.Number => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindNumber)),
        AttributeDataType.LongNumber => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindLongNumber)),
        AttributeDataType.Decimal => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindDecimal)),
        AttributeDataType.DateTime => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindDateTime)),
        AttributeDataType.Guid => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindGuid)),
        AttributeDataType.Boolean => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindBoolean)),
        AttributeDataType.Binary => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindBinary)),
        AttributeDataType.Reference => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindReference)),
        _ => ExpressionMessage.Of(nameof(ExpressionMessages.WhereKindNotSet))
    };
}
