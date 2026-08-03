// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Schema;

/// <summary>
/// How a flattened attribute's value is reached inside a SCIM resource. Recorded when the schema is
/// flattened so that reading a resource is a lookup rather than a re-parse of the SCIM path.
/// </summary>
public enum ScimValueAccess
{
    /// <summary>Read the attribute's own value, or values where it is multi-valued.</summary>
    Simple = 0,

    /// <summary>Read a named sub-attribute out of the complex value, or out of each of them.</summary>
    ComplexSubAttribute = 1,

    /// <summary>
    /// Select the entry of a multi-valued complex attribute by its canonical type (or by its primary
    /// flag), then read a named sub-attribute from it.
    /// </summary>
    CanonicalSlot = 2,

    /// <summary>
    /// Read the referenced identifiers from a complex attribute carrying <c>$ref</c>, for example group
    /// membership or a manager.
    /// </summary>
    ComplexReference = 3
}
