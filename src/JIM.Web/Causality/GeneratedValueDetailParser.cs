// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Parses a generated-value outcome's DetailMessage (Unique Value Generation, #242), following the same
/// "one tested owner for the format" precedent as <see cref="OutcomeDetailMessageParser"/>.
/// </summary>
public static class GeneratedValueDetailParser
{
    /// <summary>
    /// Parses a <c>"{attributeName}: {value}"</c> DetailMessage. Never throws: null, empty, or a message with
    /// no ": " separator (or an empty attribute name before it) yields both fields null, so the caller falls
    /// back to a generic sentence rather than rendering a malformed attribute name or value.
    /// </summary>
    public static GeneratedValueDetail Parse(string? detailMessage)
    {
        if (string.IsNullOrEmpty(detailMessage))
            return new GeneratedValueDetail(null, null);

        var separatorIndex = detailMessage.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return new GeneratedValueDetail(null, null);

        var attributeName = detailMessage[..separatorIndex];
        var value = detailMessage[(separatorIndex + 2)..];
        return new GeneratedValueDetail(attributeName, value);
    }
}
