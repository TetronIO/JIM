// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// Composes the destructive lead of the save-time acknowledgement from a configuration change preflight: the headline
/// an administrator reads first, and the per-item statements of what their particular change will do.
///
/// It lives here rather than in the dialog because it is copy selection, not markup, and copy selection is the part
/// that can be wrong in a way markup cannot: naming the wrong kind of thing, or claiming a consequence in the wrong
/// direction. See <c>ConfigurationChangePreflightConsequencesTests</c>.
/// </summary>
public static class ConfigurationChangePreflightConsequences
{
    /// <summary>
    /// The destructive consequences of <paramref name="preflight"/>, or null when there are none worth leading with.
    /// </summary>
    public static ConsequenceGroup? For(ConfigurationChangePreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        var destructive = preflight.DestructiveItems.Where(i => i.Consequence != null).ToList();
        if (destructive.Count == 0)
            return null;

        return new ConsequenceGroup
        {
            Headline = Headline(destructive),
            // No per-item icon: the alert already carries one, and two side by side reads as a rendering fault.
            Items = destructive.Select(item => new ConsequenceItem
            {
                Text = $"{LeafOf(item.Label)}: {item.Consequence}"
            }).ToList()
        };
    }

    /// <summary>
    /// The last segment of a qualified label ("Partitions &gt; dc=corp,dc=local &gt; Containers &gt; ou=Contractors,
    /// dc=corp,dc=local" becomes "ou=Contractors,dc=corp,dc=local").
    /// </summary>
    /// <remarks>
    /// The dialog lists every changing property directly beneath this alert, each under its full qualified label, so
    /// spelling the whole path out here printed the same string twice on one screen. The leaf is what identifies
    /// which consequence belongs to which item, which is all this line needs it for.
    /// </remarks>
    private static string LeafOf(string label)
    {
        var lastSeparator = label.LastIndexOf('>');
        return lastSeparator < 0 ? label : label[(lastSeparator + 1)..].Trim();
    }

    /// <summary>
    /// The headline, chosen by what the administrator actually did. Two shapes reach this dialog and they are not the
    /// same sentence: a property whose *value* decides whether objects are removed, and a whole item taken out of a
    /// collection, where the removal itself is the destructive act and the item has no value to speak of.
    ///
    /// The property wording is deliberately neutral about direction. Those properties are destructive because they
    /// decide whether objects are removed, and the same property is equally destructive turned off: switching a
    /// Deprovisioning Action back to Disconnect is still Class A, and a headline announcing data loss over a change
    /// that prevents it is the sort of lie that teaches administrators to click straight through. Which way a
    /// particular change goes is the item's own business, below. A removed collection item needs no such hedging,
    /// because only the removal is classified destructive; adding one is not.
    /// </summary>
    private static string Headline(IReadOnlyList<ConfigurationChangePreflightItem> destructive)
    {
        var single = destructive.Count == 1;

        if (destructive.All(i => i.IsCollectionItem))
            return single ? "Removing this takes objects out of scope" : "Removing these takes objects out of scope";

        // A single save can do both, and neither of the specific headlines is true of the whole. Widen rather than
        // pick one and be wrong about the other half.
        if (destructive.Any(i => i.IsCollectionItem))
            return "These changes decide whether objects are removed";

        return single
            ? "This property decides whether objects are removed"
            : "These properties decide whether objects are removed";
    }
}
