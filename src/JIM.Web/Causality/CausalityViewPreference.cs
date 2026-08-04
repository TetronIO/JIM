// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Converts between <see cref="CausalityView"/> values and the lowercase string keys the user
/// preference store persists ("flow" | "timeline" | "graph").
/// </summary>
public static class CausalityViewPreference
{
    /// <summary>
    /// The preference key for a view, e.g. "timeline".
    /// </summary>
    public static string ToKey(CausalityView view)
    {
        return view switch
        {
            CausalityView.Flow => "flow",
            CausalityView.Graph => "graph",
            _ => "timeline"
        };
    }

    /// <summary>
    /// Maps a stored preference key back to its view; null for unknown or missing values.
    /// </summary>
    public static CausalityView? FromKey(string? key)
    {
        return key switch
        {
            "flow" => CausalityView.Flow,
            "timeline" => CausalityView.Timeline,
            "graph" => CausalityView.Graph,
            _ => null
        };
    }
}
