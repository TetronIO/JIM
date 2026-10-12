// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities.DTOs;

namespace JIM.Web.Causality;

/// <summary>
/// How the Causality Explorer (#2100) names a focus in a line of text, such as a step of its trail.
/// </summary>
public static class CausalityFocusLabel
{
    /// <summary>
    /// What a focus the labels no longer find is called: history retention has removed its item, or the object and
    /// every record of its deletion are gone.
    /// </summary>
    public const string NotRetained = "No longer retained";

    /// <summary>
    /// Names a focus: an item by the object it was about and the run that recorded it, an object by its type and name.
    /// </summary>
    /// <param name="focus">The focus.</param>
    /// <param name="labels">What the explorer read for its foci; null while they have not been read.</param>
    public static string For(CausalityFocus focus, CausalFocusLabels? labels)
    {
        ArgumentNullException.ThrowIfNull(focus);
        if (labels == null)
            return Kind(focus.Kind);

        if (focus.Kind == CausalityFocusKind.Item)
            return labels.Items.TryGetValue(focus.Id, out var entry) ? ForItem(entry) : NotRetained;

        return focus.Subject is { } subject && labels.Objects.TryGetValue(subject, out var header)
            ? ForObject(header)
            : NotRetained;
    }

    /// <summary>
    /// "group: Project-Pulse · Full Synchronisation".
    /// </summary>
    public static string ForItem(CausalTimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var name = ObjectDescription.ChipName(entry.DisplayName, entry.ExternalId) ?? "Unnamed object";
        var objectLabel = ObjectDescription.ForConnectedSystemObjectLabel(entry.ObjectType, name);
        return string.IsNullOrWhiteSpace(entry.ActivityTargetName) ? objectLabel : $"{objectLabel} · {entry.ActivityTargetName}";
    }

    /// <summary>
    /// "person: tina.adams99".
    /// </summary>
    public static string ForObject(CausalObjectHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var name = ObjectDescription.ChipName(header.DisplayName, header.ExternalId) ?? "Unnamed object";
        return ObjectDescription.ForConnectedSystemObjectLabel(header.ObjectTypeName, name);
    }

    private static string Kind(CausalityFocusKind kind) => kind switch
    {
        CausalityFocusKind.ConnectedSystemObject => "Connected System Object",
        CausalityFocusKind.MetaverseObject => "Metaverse Object",
        _ => "Execution item"
    };
}
