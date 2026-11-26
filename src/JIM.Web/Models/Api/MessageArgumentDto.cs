// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Web.Models.Api;

/// <summary>
/// One value placed into a message: exactly one of text, number, date or a nested message is present.
/// </summary>
/// <remarks>
/// Mirrors how JIM stores the message (#3039), so a client can render it in any language: text is used as it is,
/// a number and a date are formatted for the reader, and a nested message is rendered the same way as its parent.
/// </remarks>
public class MessageArgumentDto
{
    /// <summary>
    /// A name or other text, used as it is in every language. Absent when the value is a number, a date or a message.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>
    /// A whole number, to be formatted for the reader. Absent when the value is text, a date or a message.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Number { get; set; }

    /// <summary>
    /// A moment in UTC, to be formatted for the reader. Absent when the value is text, a number or a message.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Date { get; set; }

    /// <summary>
    /// A message placed inside this one, rendered the same way. Absent when the value is text, a number or a date.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageDto? Message { get; set; }
}
