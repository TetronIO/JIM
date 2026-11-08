// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An attribute a Delta Synchronisation loaded without any of its values (#2818): a large group's member attribute whose
/// only reader on the page is Drift Detection, which asks the database for the values the object and its Metaverse Object
/// disagree on (<see cref="DriftHeldReferenceAnswer"/>) rather than reading them.
/// </summary>
/// <param name="AttributeId">The attribute loaded without its values.</param>
/// <param name="AttributeName">Its name.</param>
/// <param name="HoldsAny">Whether the object held any value of the attribute when it was loaded, from a count of its
/// rows.</param>
public sealed record SynchronisationPartiallyLoadedAttribute(int AttributeId, string AttributeName, bool HoldsAny)
    : PartiallyLoadedAttribute(AttributeId, AttributeName)
{
    /// <inheritdoc />
    public override bool HoldsAnyValue => HoldsAny;

    /// <summary>
    /// Not known: the load counts the rows only far enough to know the attribute is large enough to leave out.
    /// </summary>
    public override int? AtLeastValueCount => null;

    /// <inheritdoc />
    public override string Describe() =>
        "for a Delta Synchronisation's Drift Detection, which asks the database for the values the object and its Metaverse Object disagree on";
}
