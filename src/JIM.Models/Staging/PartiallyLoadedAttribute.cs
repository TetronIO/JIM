// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An attribute of a Connected System Object that was loaded in part: without all the values the object holds, beside what
/// the object is known to hold of it (<see cref="ConnectedSystemObject.PartiallyLoadedAttributes"/>). A large group's
/// member attribute is the case: export execution loads only the members its changes name
/// (<see cref="ExportPartiallyLoadedAttribute"/>, #2816), and a Delta Import only the members the database's comparison
/// left undecided (<see cref="ImportPartiallyLoadedAttribute"/>, #2817).
/// </summary>
/// <param name="AttributeId">The attribute loaded in part.</param>
/// <param name="AttributeName">Its name, for readers that know attributes by name (a Connector, the class membership
/// check).</param>
public abstract record PartiallyLoadedAttribute(int AttributeId, string AttributeName)
{
    /// <summary>
    /// Whether the object, as loaded, holds any value of the attribute.
    /// </summary>
    public abstract bool HoldsAnyValue { get; }

    /// <summary>
    /// How many values the object holds at least, where that is known; null where it is not.
    /// </summary>
    public abstract int? AtLeastValueCount { get; }

    /// <summary>
    /// What was loaded and why, for a message.
    /// </summary>
    public abstract string Describe();
}
