// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Localisation;

namespace JIM.Web.Localisation;

/// <summary>
/// How the portal shows what an expression produced (#3030): the values as text lines, in the viewer's language where
/// JIM words the text itself (a Binary value). The REST API and PowerShell keep reading <see cref="ExpressionText"/>'s
/// British English.
/// </summary>
public static class ExpressionDisplay
{
    /// <summary>An Expression's result as the lines a tester shows, one per value.</summary>
    public static IReadOnlyList<string> Lines(object? result) =>
        ExpressionText.ToDisplayLines(result, message => message.ToCurrentLanguage());

    /// <summary>One value as a line a tester shows.</summary>
    public static string Line(object value) =>
        ExpressionText.DescribeMessage(value)?.ToCurrentLanguage() ?? ExpressionText.Describe(value);
}
