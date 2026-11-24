// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Models.Localisation;

/// <summary>
/// One value in a <see cref="JimMessage"/>: text, a number or a moment, kept typed so the reader's language formats
/// it (1,234 or 1 234; 12 May or 12 mai) and so it survives being stored as JSON. Exactly one property is set.
/// </summary>
public sealed record MessageArgument
{
    /// <summary>Text, shown as it is: a name, an identifier, an attribute value. Never translated.</summary>
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>A whole number, shown with the reader's digit grouping.</summary>
    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Number { get; init; }

    /// <summary>A moment (UTC), shown in the reader's local time and date format.</summary>
    [JsonPropertyName("d")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Utc { get; init; }

    /// <summary>
    /// Another message, shown in the same language: a reason inside a refusal ("Could not save: {0}").
    /// </summary>
    [JsonPropertyName("m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JimMessage? Message { get; init; }

    public static implicit operator MessageArgument(string? text) => new() { Text = text ?? string.Empty };

    public static implicit operator MessageArgument(int number) => new() { Number = number };

    public static implicit operator MessageArgument(long number) => new() { Number = number };

    public static implicit operator MessageArgument(DateTime utc) =>
        new() { Utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc) };

    public static implicit operator MessageArgument(JimMessage message) => new() { Message = message };
}
