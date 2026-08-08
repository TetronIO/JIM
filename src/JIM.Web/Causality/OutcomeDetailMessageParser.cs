// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Parses the overloaded DetailMessage channel on sync outcomes, extracted from the inline logic
/// previously in OutcomeTreeNode.razor so the format has one tested owner. The first pipe-delimited
/// segment is treated as a Connected System id when it parses as an integer (matching the tree's
/// int.TryParse behaviour exactly); otherwise the whole message is plain contextual text.
/// </summary>
public static class OutcomeDetailMessageParser
{
    /// <summary>
    /// Parses a sync outcome DetailMessage. Never throws; null or empty input yields an empty result.
    /// </summary>
    public static OutcomeDetailMessage Parse(string? detailMessage)
    {
        if (string.IsNullOrEmpty(detailMessage))
            return new OutcomeDetailMessage(null, null, null);

        var parts = detailMessage.Split('|');
        if (int.TryParse(parts[0], out var connectedSystemId))
        {
            var csoTypeName = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
            return new OutcomeDetailMessage(connectedSystemId, csoTypeName, null);
        }

        return new OutcomeDetailMessage(null, null, detailMessage);
    }
}
