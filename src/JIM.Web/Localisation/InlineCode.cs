// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;

namespace JIM.Web.Localisation;

/// <summary>
/// A name or value placed into a sentence of portal text, shown as inline code (<c>jim-code-inline</c>): an attribute
/// value, an attribute or object name, a column or a setting, wherever it sits in running text rather than in an input
/// or a table cell of its own. The code styling marks where the value starts and ends, so the text puts no quotes
/// around it. Each value is HTML-encoded, since the message is rendered as markup and values come from directories and
/// administrators.
/// </summary>
public static class InlineCode
{
    /// <summary>One value as inline code markup, encoded.</summary>
    public static string Markup(string? text) => $"<code class=\"jim-code-inline\">{WebUtility.HtmlEncode(text)}</code>";

    /// <summary>
    /// A localised message with each of <paramref name="values"/> in place of its placeholder as inline code, for a
    /// dialog's <c>MarkupMessage</c> or a snackbar. The template is the portal's own resource text, never a value.
    /// </summary>
    public static MarkupString Format(string template, params string?[] values) =>
        (MarkupString)string.Format(CultureInfo.CurrentCulture, template, values.Select(v => (object)Markup(v)).ToArray());
}
