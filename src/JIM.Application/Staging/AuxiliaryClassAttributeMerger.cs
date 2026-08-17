// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Staging;

/// <summary>
/// Brings the attributes of the auxiliary classes an administrator has selected onto the structural Object Type they
/// extend.
/// </summary>
/// <remarks>
/// On an RFC 4512 directory an auxiliary class attaches to an entry, not to the schema, so no amount of reading the
/// directory will tell JIM that a person entry carries posixAccount's attributes. Only an administrator can say so,
/// and this is what turns that statement into attributes a Synchronisation Rule can map.
/// <para>
/// It reconciles in both directions rather than only adding, so that it gives the same answer whether it runs after
/// a schema refresh or straight after a selection changes. Its own work is recognised by an attribute's
/// <see cref="ConnectedSystemObjectTypeAttribute.ClassName"/>: schema discovery stamps every attribute with the
/// class its Object Type was built from, so an attribute on a structural type bearing another class's name can only
/// have arrived through a merge.
/// </para>
/// </remarks>
internal static class AuxiliaryClassAttributeMerger
{
    internal static AuxiliaryClassMergeResult Merge(ConnectedSystem connectedSystem)
    {
        var result = new AuxiliaryClassMergeResult();
        var objectTypes = connectedSystem.ObjectTypes;
        if (objectTypes == null || objectTypes.Count == 0)
            return result;

        var objectTypesById = objectTypes
            .GroupBy(objectType => objectType.Id)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var baseType in objectTypes)
        {
            var contributors = ResolveContributors(baseType, objectTypesById, result);
            RemoveAttributesNoLongerContributed(baseType, contributors, result);
            AddAttributesFromContributors(baseType, contributors, result);
        }

        return result;
    }

    /// <summary>
    /// The auxiliary Object Types currently selected for a base type, keyed by name.
    /// </summary>
    /// <remarks>
    /// An extension may point at a type that this refresh has removed from the schema. The database cascade takes
    /// the selection with it, but the merge runs on the in-memory graph before that happens, so the gap is reported
    /// and skipped rather than thrown on: a directory dropping a class must not stop the rest of the schema landing.
    /// </remarks>
    private static Dictionary<string, ConnectedSystemObjectType> ResolveContributors(
        ConnectedSystemObjectType baseType,
        Dictionary<int, ConnectedSystemObjectType> objectTypesById,
        AuxiliaryClassMergeResult result)
    {
        var contributors = new Dictionary<string, ConnectedSystemObjectType>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in baseType.Extensions)
        {
            if (!objectTypesById.TryGetValue(extension.ExtensionObjectTypeId, out var extensionType))
            {
                result.UnresolvedExtensions.Add(
                    $"{baseType.Name} extends object type {extension.ExtensionObjectTypeId}, which the schema no longer contains.");
                continue;
            }

            contributors[extensionType.Name] = extensionType;
        }

        return contributors;
    }

    /// <summary>
    /// Takes back off any attribute this merger previously added on behalf of a class that no longer contributes.
    /// </summary>
    private static void RemoveAttributesNoLongerContributed(
        ConnectedSystemObjectType baseType,
        Dictionary<string, ConnectedSystemObjectType> contributors,
        AuxiliaryClassMergeResult result)
    {
        if (baseType.Attributes == null)
            return;

        // An attribute naming the type's own class is native, whatever else is going on; only attributes naming
        // some other class are candidates, and only when that class is not contributing any more.
        var stale = baseType.Attributes
            .Where(attribute => attribute.ClassName != null &&
                                !attribute.ClassName.Equals(baseType.Name, StringComparison.OrdinalIgnoreCase) &&
                                !contributors.ContainsKey(attribute.ClassName))
            .ToList();

        if (stale.Count == 0)
            return;

        foreach (var attribute in stale)
            baseType.Attributes.Remove(attribute);

        result.RemovedAttributes[baseType.Name] = stale.Select(attribute => attribute.Name).ToList();
    }

    private static void AddAttributesFromContributors(
        ConnectedSystemObjectType baseType,
        Dictionary<string, ConnectedSystemObjectType> contributors,
        AuxiliaryClassMergeResult result)
    {
        if (contributors.Count == 0)
            return;

        baseType.Attributes ??= [];
        var present = new HashSet<string>(baseType.Attributes.Select(attribute => attribute.Name), StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();

        foreach (var contributor in contributors.Values)
        {
            foreach (var attribute in contributor.Attributes ?? [])
            {
                // The structural class's own attribute wins, and so does the first auxiliary class to declare one
                // two of them share: an Object Type carrying the same attribute name twice would be ambiguous
                // everywhere downstream, and the surviving row is the one Synchronisation Rules already reference.
                var isFirstClaimToTheName = present.Add(attribute.Name);

                if (!isFirstClaimToTheName)
                    continue;

                baseType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = attribute.Name,
                    Description = attribute.Description,
                    AttributePlurality = attribute.AttributePlurality,
                    Type = attribute.Type,
                    Writability = attribute.Writability,

                    // The auxiliary class's own requirement travels with the attribute: an entry that carries the
                    // class must satisfy its MUSTs, whichever structural class it is merged onto.
                    Required = attribute.Required,

                    // Provenance, and the marker this merger recognises its own work by.
                    ClassName = contributor.Name

                    // Deliberately not Selected: choosing an auxiliary class says its attributes are available, not
                    // that JIM should manage every one of them.
                });

                added.Add(attribute.Name);
            }
        }

        if (added.Count > 0)
            result.AddedAttributes[baseType.Name] = added;
    }
}

/// <summary>
/// What merging an administrator's auxiliary class selections changed, so the schema refresh result and the portal
/// can say so rather than leaving an administrator to spot it.
/// </summary>
internal class AuxiliaryClassMergeResult
{
    /// <summary>Attribute names added to each Object Type, keyed by Object Type name.</summary>
    public Dictionary<string, List<string>> AddedAttributes { get; } = new();

    /// <summary>Attribute names taken back off each Object Type, keyed by Object Type name.</summary>
    public Dictionary<string, List<string>> RemovedAttributes { get; } = new();

    /// <summary>
    /// Selections naming an Object Type the schema no longer contains, described one per line.
    /// </summary>
    public List<string> UnresolvedExtensions { get; } = [];
}
