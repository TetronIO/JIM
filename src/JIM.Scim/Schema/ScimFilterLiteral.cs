// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using JIM.Models.Core;

namespace JIM.Scim.Schema;

/// <summary>
/// Writes a comparison value into a SCIM filter (RFC 7644 section 3.4.2.2), whether the filter is a search
/// (<c>userName eq "alice"</c>) or a PATCH path selecting one value (<c>members[value eq "id"]</c>).
/// <para>
/// One place, because a value written into filter grammar unescaped stops being a value: a quote ends the string
/// early, a trailing backslash swallows the closing quote, and either way the filter selects something other than what
/// JIM meant, or nothing.
/// </para>
/// </summary>
public static class ScimFilterLiteral
{
    /// <summary>
    /// The value as a filter comparison literal for an attribute of the given type: a number or a boolean unquoted,
    /// everything else (strings, dateTimes, binaries, references) as a JSON string in the form
    /// <see cref="ScimValueFormatter"/> sends the value itself, so the filter compares like with like.
    /// </summary>
    public static string For(object value, AttributeDataType type)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Only a value that really is a number goes in unquoted. Text arriving for a numeric attribute is quoted
        // like any other, because written bare it would be filter grammar rather than a value.
        var unquoted = type switch
        {
            AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Decimal =>
                value is int or long or decimal or short or byte,
            AttributeDataType.Boolean => value is bool,
            _ => false
        };

        var text = ScimValueFormatter.ToText(value) ?? string.Empty;
        return unquoted ? text : Quote(text);
    }

    /// <summary>
    /// A comparison value as RFC 7644 section 3.4.2.2 has it: a JSON string. The quote and the backslash are escaped,
    /// and control characters written as <c>\u</c> sequences, so whatever a value holds it stays one value and can
    /// never become filter grammar. Everything else is left as it is, since a provider's filter parser need not decode
    /// escapes JSON does not require.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(character))
                        quoted.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        quoted.Append(character);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
