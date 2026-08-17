// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Builds the list of auxiliary classes offered against a structural Connected System Object Type, in the order an
/// administrator wants to read them.
/// </summary>
/// <remarks>
/// Shared by every surface that offers the choice (the portal, the REST API and PowerShell) so that all three list
/// the same classes, describe them the same way and order them the same way.
/// </remarks>
public static class AuxiliaryClassOfferBuilder
{
    /// <summary>
    /// The auxiliary classes that may be merged into <paramref name="objectType"/>, or an empty list when merging
    /// means nothing for it.
    /// </summary>
    /// <param name="objectType">The Object Type the offers are for, with its tags and extensions loaded.</param>
    /// <param name="allObjectTypes">Every Object Type in the Connected System's schema, with their tags loaded.</param>
    /// <param name="latestDiscoveryRun">The last auxiliary class discovery run, with its results, or null when none
    /// has been run. A cancelled run's partial results count, because a class it did observe is genuinely in use.</param>
    public static List<AuxiliaryClassOffer> Build(
        ConnectedSystemObjectType objectType,
        IEnumerable<ConnectedSystemObjectType> allObjectTypes,
        AuxiliaryClassDiscoveryRun? latestDiscoveryRun = null)
    {
        // Merging only means something where JIM computes class membership; a Connected System without the concept
        // has nowhere to write the class, and an auxiliary class cannot itself be extended.
        if (!objectType.ManagesClassMembership() || objectType.IsAuxiliary())
            return [];

        var merged = objectType.Extensions
            .Select(extension => extension.ExtensionObjectTypeId)
            .ToHashSet();

        var permitted = objectType.Tags
            .Where(tag => tag.Key == ObjectTypeTags.Keys.PermittedAuxiliaryClass)
            .Select(tag => tag.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var observed = (latestDiscoveryRun?.Results ?? [])
            .Where(result => result.StructuralObjectTypeId == objectType.Id)
            .GroupBy(result => result.AuxiliaryClassName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(result => result.EntryCount), StringComparer.OrdinalIgnoreCase);

        // Merged first, because those are what the Object Type is currently made of; then whatever is suggested,
        // most widely used first; then the rest of the schema's auxiliary classes by name, so a class an
        // administrator knows the name of can always be found.
        return
        [
            .. allObjectTypes
                .Where(candidate => candidate.IsAuxiliary())
                .Select(candidate => new AuxiliaryClassOffer
                {
                    ObjectType = candidate,
                    Merged = merged.Contains(candidate.Id),
                    ContributedAttributeCount = candidate.Attributes.Count,
                    PermittedByTheConnectedSystem = permitted.Contains(candidate.Name),
                    EntriesObservedOn = observed.TryGetValue(candidate.Name, out var entryCount) ? entryCount : null
                })
                .OrderByDescending(offer => offer.Merged)
                .ThenByDescending(offer => offer.IsSuggested)
                .ThenByDescending(offer => offer.EntriesObservedOn ?? 0)
                .ThenBy(offer => offer.ObjectType.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// The Object Types that may carry an auxiliary-typed Object Type on an entry of their own, i.e. the choices for
    /// its Structural Carrier Class.
    /// </summary>
    /// <remarks>
    /// Only structural classes qualify: an entry has exactly one, and it is what the entry fundamentally is. An
    /// abstract or unclassified class cannot be instantiated, so offering one would produce an export the Connected
    /// System refuses.
    /// </remarks>
    public static List<ConnectedSystemObjectType> CarrierCandidates(IEnumerable<ConnectedSystemObjectType> allObjectTypes)
    {
        return
        [
            .. allObjectTypes
                .Where(candidate => candidate.IsStructural())
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
