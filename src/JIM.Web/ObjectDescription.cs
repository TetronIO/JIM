// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web;

/// <summary>
/// How a Connected System Object or a Metaverse Object is named wherever the portal mentions one in
/// running text (#1669), so a reader never needs to know what a "Connected System Object" is to follow
/// the sentence: "user Baseline User in Panoply AD" reads as an account in a directory without the
/// reader knowing JIM's own vocabulary for it. The single source of the format: no page builds this
/// string by hand.
/// </summary>
/// <remarks>
/// Two shapes per side, chosen by whether the object's name is already shown elsewhere on the same
/// row or line:
/// <list type="bullet">
/// <item>A full mention, name included, for a sentence that has not named the object yet
/// (<see cref="ForConnectedSystemObject"/>, <see cref="ForMetaverseObject"/>).</item>
/// <item>A "place" sub-line, name omitted, for beneath a name a table cell or detail row already shows
/// (<see cref="ForConnectedSystemObjectPlace(string?, string?)"/>, <see cref="ForMetaverseObjectPlace(string?)"/>).</item>
/// </list>
/// The type name is printed exactly as the schema gives it (lower-case, where that is how it is
/// stored); it is never re-cased. When the type is unknown, the full product noun ("Connected System
/// Object" / "Metaverse Object") stands in for it instead.
/// </remarks>
public static class ObjectDescription
{
    /// <summary>
    /// A Connected System Object mentioned in running text, with its name: "user Baseline User in
    /// Panoply AD". Falls back to omitting whichever of the type or the Connected System is unknown,
    /// down to "Connected System Object {name}" when both are.
    /// </summary>
    /// <param name="typeName">The Connected System Object Type's name, as the schema gives it, or null/empty when unknown.</param>
    /// <param name="name">The object's own name or identifier.</param>
    /// <param name="connectedSystemName">The Connected System the object lives in, or null/empty when unknown.</param>
    public static string ForConnectedSystemObject(string? typeName, string name, string? connectedSystemName)
    {
        var hasType = !string.IsNullOrWhiteSpace(typeName);
        var hasSystem = !string.IsNullOrWhiteSpace(connectedSystemName);

        if (hasType && hasSystem)
            return $"{typeName} {name} in {connectedSystemName}";
        if (hasSystem)
            return $"{name} in {connectedSystemName}";
        if (hasType)
            return $"{typeName} {name}";
        return $"Connected System Object {name}";
    }

    /// <summary>
    /// A Connected System Object's type and name as a label rather than running text: "user: Baseline
    /// User". For a column head or a chip's own name, which read as a tag rather than a sentence, so the
    /// colon stays (unlike <see cref="ForConnectedSystemObject"/>'s "user Baseline User in Panoply AD").
    /// Falls back to the bare name when the type is unknown.
    /// </summary>
    /// <param name="typeName">The Connected System Object Type's name, as the schema gives it, or null/empty when unknown.</param>
    /// <param name="name">The object's own name or identifier.</param>
    public static string ForConnectedSystemObjectLabel(string? typeName, string name)
    {
        return !string.IsNullOrWhiteSpace(typeName)
            ? $"{typeName}: {name}"
            : name;
    }

    /// <summary>
    /// A Connected System Object's type and Connected System, with no name, for a sub-line beneath a
    /// name a table cell or detail row already shows: "user in Panoply AD". Falls back to whichever of
    /// the two is known, down to the bare product noun when neither is.
    /// </summary>
    /// <param name="typeName">The Connected System Object Type's name, as the schema gives it, or null/empty when unknown.</param>
    /// <param name="connectedSystemName">The Connected System the object lives in, or null/empty when unknown.</param>
    public static string ForConnectedSystemObjectPlace(string? typeName, string? connectedSystemName)
    {
        var hasType = !string.IsNullOrWhiteSpace(typeName);
        var hasSystem = !string.IsNullOrWhiteSpace(connectedSystemName);

        if (hasType && hasSystem)
            return $"{typeName} in {connectedSystemName}";
        if (hasSystem)
            return $"in {connectedSystemName}";
        if (hasType)
            return typeName!;
        return "Connected System Object";
    }

    /// <summary>
    /// A Connected System Object's Connected System alone, with no type and no name, for a sub-line
    /// beneath a chip or heading that already names both: "in Panoply AD" (#1669 follow-up). Prefer
    /// <see cref="ForConnectedSystemObjectPlace(string?, string?)"/> when the type is not shown
    /// elsewhere on the row; repeating it here would say it twice. Falls back to the bare product noun
    /// when the Connected System is unknown.
    /// </summary>
    /// <param name="connectedSystemName">The Connected System the object lives in, or null/empty when unknown.</param>
    public static string ForConnectedSystemObjectPlace(string? connectedSystemName)
    {
        return !string.IsNullOrWhiteSpace(connectedSystemName)
            ? $"in {connectedSystemName}"
            : "Connected System Object";
    }

    /// <summary>
    /// A Metaverse Object mentioned in running text, with its name: "User Baseline User". Falls back to
    /// the full product noun when the type is unknown.
    /// </summary>
    /// <param name="typeName">The Metaverse Object Type's name, or null/empty when unknown.</param>
    /// <param name="name">The object's own name or identifier.</param>
    public static string ForMetaverseObject(string? typeName, string name)
    {
        return !string.IsNullOrWhiteSpace(typeName)
            ? $"{typeName} {name}"
            : $"Metaverse Object {name}";
    }

    /// <summary>
    /// A Metaverse Object's type, with no name, for a sub-line beneath a name a table cell or detail row
    /// already shows: "User in the Metaverse". Falls back to the bare product noun when the type is
    /// unknown.
    /// </summary>
    /// <param name="typeName">The Metaverse Object Type's name, or null/empty when unknown.</param>
    public static string ForMetaverseObjectPlace(string? typeName)
    {
        return !string.IsNullOrWhiteSpace(typeName)
            ? $"{typeName} in the Metaverse"
            : "Metaverse Object";
    }

    /// <summary>
    /// A Metaverse Object's place alone, with no type and no name, for a sub-line beneath a chip or
    /// heading that already names both: "in the Metaverse" (#1669 follow-up). Every Metaverse Object
    /// lives in the one Metaverse, so unlike the other "place" methods this has no unknown-value
    /// fallback to make: it always reads the same.
    /// </summary>
    public static string ForMetaverseObjectPlace()
    {
        return "in the Metaverse";
    }

    /// <summary>
    /// The one naming rule for an object chip: the display name if present, else the external id, else
    /// null when neither is known (a provisioned object before export names nothing; the call site shows the
    /// type alone and links the object beside it). Never both at once: the external id, where distinct, belongs
    /// in the chip's tooltip (<see cref="ForConnectedSystemObjectChipTooltip"/>), not beside the name in the
    /// chip's own text.
    /// </summary>
    /// <param name="displayName">The object's display name, or null/empty when it has none.</param>
    /// <param name="externalId">The object's external id, or null/empty when it has none.</param>
    public static string? ChipName(string? displayName, string? externalId)
    {
        return Present(displayName) ?? Present(externalId);
    }

    /// <summary>
    /// A Connected System Object chip's tooltip: "person: Sienna Quinn · EMP000051 · in HR CSV Source".
    /// Carries what the chip's own text leaves out: the external id, when it differs from the name the chip
    /// shows, and the Connected System the object lives in.
    /// </summary>
    /// <param name="typeName">The Connected System Object Type's name, as the schema gives it, or null/empty when unknown.</param>
    /// <param name="displayName">The object's display name, or null/empty when it has none.</param>
    /// <param name="externalId">The object's external id, or null/empty when it has none.</param>
    /// <param name="connectedSystemName">The Connected System the object lives in, or null/empty when unknown.</param>
    public static string ForConnectedSystemObjectChipTooltip(
        string? typeName, string? displayName, string? externalId, string? connectedSystemName)
    {
        var name = ChipName(displayName, externalId);
        var parts = new List<string>
        {
            name != null
                ? ForConnectedSystemObjectLabel(typeName, name)
                : Present(typeName) ?? "Connected System Object"
        };

        var presentExternalId = Present(externalId);
        if (presentExternalId != null && !string.Equals(presentExternalId, name, StringComparison.Ordinal))
            parts.Add(presentExternalId);

        var presentSystem = Present(connectedSystemName);
        if (presentSystem != null)
            parts.Add($"in {presentSystem}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A Metaverse Object chip's tooltip: "User: Sienna Quinn". A Metaverse Object carries no external
    /// id of its own, so there is nothing this adds over <see cref="ForConnectedSystemObjectLabel"/> beyond the
    /// unknown-value fallback that makes it safe to call with either value missing.
    /// </summary>
    /// <param name="typeName">The Metaverse Object Type's name, or null/empty when unknown.</param>
    /// <param name="displayName">The object's display name, or null/empty when it has none.</param>
    public static string ForMetaverseObjectChipTooltip(string? typeName, string? displayName)
    {
        var name = Present(displayName);
        return name != null
            ? ForConnectedSystemObjectLabel(typeName, name)
            : Present(typeName) ?? "Metaverse Object";
    }

    /// <summary>
    /// Treats a whitespace-only value as absent: a connected system that supplies "   " has supplied nothing,
    /// and rendering it produces a label that looks empty but is not.
    /// </summary>
    private static string? Present(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
