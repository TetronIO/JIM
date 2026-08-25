// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Staging;

/// <summary>
/// Turns what a sample of a Connected System's objects carried into suggestions an administrator can act on.
/// </summary>
/// <remarks>
/// A Connector counts every class it saw rather than filtering to the auxiliary ones, because which classes are
/// auxiliary is JIM's knowledge (the classification tags) and not the Connector's. This is where that knowledge is
/// applied: the structural class the sample was taken from is on every object of that type by definition, abstract
/// classes cannot be instantiated, and an unclassified type is one JIM knows nothing about. None of those is
/// something an administrator could sensibly attach, so only auxiliary classes survive.
/// </remarks>
internal static class AuxiliaryClassUsageAggregator
{
    internal static AuxiliaryClassUsageAggregation Aggregate(
        ConnectedSystemObjectType structuralObjectType,
        ObjectClassUsageResult usage,
        IEnumerable<ConnectedSystemObjectType> objectTypes)
    {
        var aggregation = new AuxiliaryClassUsageAggregation();

        var objectTypesByName = objectTypes
            .GroupBy(objectType => objectType.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (className, entryCount) in usage.ObjectClassCounts)
        {
            if (!objectTypesByName.TryGetValue(className, out var objectType))
            {
                // An object carrying a class the schema does not publish is a Connected System contradicting
                // itself. There is no Object Type to attach, so it cannot be suggested, but staying silent would
                // leave an administrator wondering why a class they can see on their own objects never appears.
                aggregation.UnrecognisedClasses.Add(className);
                continue;
            }

            if (!objectType.IsAuxiliary())
                continue;

            aggregation.Results.Add(new AuxiliaryClassDiscoveryResult
            {
                StructuralObjectTypeId = structuralObjectType.Id,

                // The schema's spelling, not the object's: that is what matches an Object Type.
                AuxiliaryClassName = objectType.Name,
                EntryCount = entryCount
            });
        }

        // Most-used first, so an administrator meets the class most of their objects carry before the long tail.
        aggregation.Results.Sort((left, right) => right.EntryCount.CompareTo(left.EntryCount));
        return aggregation;
    }
}

/// <summary>
/// What a sample of one structural type's objects suggests, and what it could not make sense of.
/// </summary>
internal class AuxiliaryClassUsageAggregation
{
    /// <summary>
    /// One per auxiliary class the sampled objects carried, most-used first. Not yet attached to a discovery run.
    /// </summary>
    public List<AuxiliaryClassDiscoveryResult> Results { get; } = [];

    /// <summary>
    /// Classes the sampled objects carried that the Connected System's schema does not publish.
    /// </summary>
    public List<string> UnrecognisedClasses { get; } = [];
}
