// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Converts between <see cref="CausalityView"/> values and the lowercase string keys the user
/// preference store persists ("lineage" | "timeline"). The retired Flow and Graph views' keys
/// ("flow", "graph"), and "spine", the name the Lineage view carried in development, deliberately
/// map to null, so a preference stored before the retirement or the rename resolves to the panel's
/// default without being overwritten. That is the right answer for "spine" either way, since the
/// view it named is the default.
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
            CausalityView.Lineage => "lineage",
            _ => "timeline"
        };
    }

    /// <summary>
    /// Maps a stored preference key back to its view; null for unknown, retired or missing values.
    /// </summary>
    public static CausalityView? FromKey(string? key)
    {
        return key switch
        {
            "timeline" => CausalityView.Timeline,
            "lineage" => CausalityView.Lineage,
            _ => null
        };
    }
}
