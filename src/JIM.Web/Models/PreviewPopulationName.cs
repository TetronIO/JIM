// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// Names the population a Configuration Change Preview summary row covers (#1275).
///
/// A row covers many objects, so the type is written in the plural: "Users", not "User". Where the row is about a
/// Metaverse Object Type, the plural is the one an administrator authored on the type itself, because that is the
/// only source that knows a Person is one of several People. Where it is a Connected System Object Type there is no
/// authored plural to read, because the name is a class the Connector discovered rather than anything JIM named, so
/// the general rule applies.
/// </summary>
public static class PreviewPopulationName
{
    /// <summary>
    /// The plural form of a summary row's object type name.
    /// </summary>
    /// <param name="typeName">The type name the preview snapshotted when it ran.</param>
    /// <param name="metaverseObjectTypeId">
    /// The Metaverse Object Type the row is about, where it is about one. Null on a row covering Connected System
    /// Objects, which is how the two are told apart: the same field carries either name.
    /// </param>
    /// <param name="authoredPluralNames">Plural names by Metaverse Object Type id, as the administrator wrote them.</param>
    public static string Pluralised(
        string? typeName,
        int? metaverseObjectTypeId,
        IReadOnlyDictionary<int, string> authoredPluralNames)
    {
        ArgumentNullException.ThrowIfNull(authoredPluralNames);

        if (string.IsNullOrEmpty(typeName))
            return string.Empty;

        // The authored plural wins where there is one. A type deleted since the preview ran has no entry, which is
        // why the snapshotted name is still the thing being pluralised rather than a second lookup of the name.
        if (metaverseObjectTypeId is { } id &&
            authoredPluralNames.TryGetValue(id, out var authored) &&
            !string.IsNullOrEmpty(authored))
        {
            return authored;
        }

        return typeName.Pluralise();
    }
}
