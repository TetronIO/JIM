// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;

namespace JIM.Models.Localisation;

/// <summary>
/// How a <see cref="JimMessage"/> is stored beside the English text it renders (#3039): as compact JSON in a
/// <c>...DetailJson</c> column, so a record written once can be read in any language later. A value that cannot be read
/// (a message from a newer version, a hand-edited row) reads as no detail, and the reader falls back to the English.
/// </summary>
public static class MessageJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General) { WriteIndented = false };

    /// <summary>The message as JSON, or null for no message.</summary>
    public static string? Serialise(JimMessage? message) =>
        message == null ? null : JsonSerializer.Serialize(message, Options);

    /// <summary>The message stored as JSON, or null when there is none or it cannot be read.</summary>
    public static JimMessage? Deserialise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JimMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
