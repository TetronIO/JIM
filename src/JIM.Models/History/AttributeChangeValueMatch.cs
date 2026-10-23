// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.History;

/// <summary>
/// The typed value an attribute condition matches, parsed from what an administrator typed by
/// <see cref="AttributeChangeConditionParser"/>. Exactly one group of members is set, chosen by the attribute's data type.
/// </summary>
public class AttributeChangeValueMatch
{
    /// <summary>
    /// The text to match, for Text attributes (against the value) and Reference attributes (against the referenced
    /// Metaverse Object's display name). Matched case-insensitively.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Whether <see cref="Text"/> contains <c>*</c>, which matches any run of characters. Without one the whole value
    /// must match.
    /// </summary>
    public bool TextHasWildcard { get; set; }

    public int? IntValue { get; set; }

    public long? LongValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public bool? BoolValue { get; set; }

    public Guid? GuidValue { get; set; }

    /// <summary>
    /// The start of the matched date and time range (inclusive, UTC).
    /// </summary>
    public DateTime? DateTimeFrom { get; set; }

    /// <summary>
    /// The end of the matched date and time range (exclusive, UTC): the next day for a date, the next second for a
    /// date and time.
    /// </summary>
    public DateTime? DateTimeTo { get; set; }
}
