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
}
