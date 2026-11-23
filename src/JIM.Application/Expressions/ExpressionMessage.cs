// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Localisation;

namespace JIM.Application.Expressions;

/// <summary>
/// Builds the messages the expression code tells an administrator (#3030): every one is a key in
/// <c>ExpressionMessages</c> with its arguments, so the portal renders it in the viewer's language while the REST API,
/// logs and stored text read the same British English as before (<see cref="MessageRenderer.ToEnglish"/>).
/// </summary>
internal static class ExpressionMessage
{
    /// <summary>A message in the expression catalogue; <paramref name="key"/> is written <c>nameof(ExpressionMessages.X)</c>.</summary>
    internal static JimMessage Of(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Expressions, key, arguments);

    /// <summary>A counted message: the catalogue holds <c>XOne</c> and <c>XOther</c>.</summary>
    internal static JimMessage Counted(string oneKey, long count, params MessageArgument[] arguments) =>
        JimMessage.Counted(ApplicationMessageCatalogues.Expressions, oneKey, count, arguments);

    /// <summary>
    /// The <see cref="ArgumentException"/> a function raises for an argument it cannot use: its message is the English,
    /// and the evaluator turns it into the expression error with the message attached, keeping the exception as the
    /// inner one.
    /// </summary>
    internal static ArgumentException Argument(JimMessage detail) => ExceptionMessage.Attach(new ArgumentException(detail.ToEnglish()), detail);

    /// <summary>As <see cref="Argument"/>, for text that is not in the format it must be in.</summary>
    internal static FormatException Format(JimMessage detail) => ExceptionMessage.Attach(new FormatException(detail.ToEnglish()), detail);

    /// <summary>A whole number written as plain digits, as the sentences it sits in always wrote it.</summary>
    internal static string Digits(long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Items as a comma separated list, in the reader's language: nested pairs, so the separator is the catalogue's.</summary>
    internal static MessageArgument List(IEnumerable<MessageArgument> items)
    {
        MessageArgument? joined = null;
        foreach (var item in items)
            joined = joined is null ? item : Of(nameof(ExpressionMessages.ListJoin), joined, item);

        return joined ?? string.Empty;
    }

    /// <summary>An error's text for a sentence: its message where it has one, else its own text (a parser's or the runtime's).</summary>
    internal static MessageArgument Reason(ExpressionEvaluationException? exception) =>
        exception?.Detail is { } detail ? detail : exception?.Message ?? string.Empty;

    /// <summary>An attribute data type's name inside a sentence ("a Number attribute").</summary>
    internal static JimMessage DataType(AttributeDataType type) => type switch
    {
        AttributeDataType.NotSet => Of(nameof(ExpressionMessages.DataTypeNotSet)),
        AttributeDataType.Text => Of(nameof(ExpressionMessages.DataTypeText)),
        AttributeDataType.Number => Of(nameof(ExpressionMessages.DataTypeNumber)),
        AttributeDataType.DateTime => Of(nameof(ExpressionMessages.DataTypeDateTime)),
        AttributeDataType.Binary => Of(nameof(ExpressionMessages.DataTypeBinary)),
        AttributeDataType.Reference => Of(nameof(ExpressionMessages.DataTypeReference)),
        AttributeDataType.Guid => Of(nameof(ExpressionMessages.DataTypeGuid)),
        AttributeDataType.Boolean => Of(nameof(ExpressionMessages.DataTypeBoolean)),
        AttributeDataType.LongNumber => Of(nameof(ExpressionMessages.DataTypeLongNumber)),
        AttributeDataType.Decimal => Of(nameof(ExpressionMessages.DataTypeDecimal)),
        _ => Of(nameof(ExpressionMessages.DataTypeNotSet))
    };
}
