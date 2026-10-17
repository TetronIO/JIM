// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Scim.Filtering;

/// <summary>
/// A comparison value in a SCIM filter: a JSON string, number, boolean or null. A number keeps the text it was written
/// as, so a value never passes through a binary floating-point type on its way through the grammar.
/// </summary>
/// <param name="Kind">The JSON kind of the value.</param>
/// <param name="Text">The string, the number as written, <c>true</c> or <c>false</c>; null for <see cref="ScimFilterValueKind.Null"/>.</param>
public sealed record ScimFilterValue(ScimFilterValueKind Kind, string? Text)
{
    /// <summary>The JSON null literal.</summary>
    public static ScimFilterValue Null { get; } = new(ScimFilterValueKind.Null, null);

    public static ScimFilterValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ScimFilterValue(ScimFilterValueKind.String, value);
    }

    /// <summary>
    /// A number exactly as JSON writes it (<c>-12.5e3</c>). The text is not validated here; the parser only produces
    /// valid JSON numbers.
    /// </summary>
    public static ScimFilterValue Number(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ScimFilterValue(ScimFilterValueKind.Number, value);
    }

    public static ScimFilterValue Number(decimal value) =>
        new(ScimFilterValueKind.Number, value.ToString(CultureInfo.InvariantCulture));

    public static ScimFilterValue Boolean(bool value) =>
        new(ScimFilterValueKind.Boolean, value ? "true" : "false");
}
