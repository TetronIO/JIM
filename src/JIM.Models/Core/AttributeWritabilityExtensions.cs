// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Ordering over <see cref="AttributeWritability"/>, for the places that have to combine two writabilities
/// into one (a complex SCIM attribute's parent and sub-attribute, for example).
/// </summary>
public static class AttributeWritabilityExtensions
{
    /// <summary>
    /// Returns the more restrictive of two writabilities. The ordering, from most restrictive to least,
    /// is <see cref="AttributeWritability.ReadOnly"/>, then <see cref="AttributeWritability.WritableOnCreate"/>,
    /// then <see cref="AttributeWritability.Writable"/>: a value that cannot be written at all is tighter
    /// than one that can be written once, which is tighter than one that can be written freely. The
    /// operation is commutative.
    /// </summary>
    /// <param name="first">The first writability.</param>
    /// <param name="second">The second writability.</param>
    /// <returns>The more restrictive of the two.</returns>
    public static AttributeWritability MostRestrictive(this AttributeWritability first, AttributeWritability second)
    {
        if (first == AttributeWritability.ReadOnly || second == AttributeWritability.ReadOnly)
            return AttributeWritability.ReadOnly;

        if (first == AttributeWritability.WritableOnCreate || second == AttributeWritability.WritableOnCreate)
            return AttributeWritability.WritableOnCreate;

        return AttributeWritability.Writable;
    }
}
