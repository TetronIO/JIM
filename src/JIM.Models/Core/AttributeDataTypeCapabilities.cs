// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// What each attribute data type is capable of holding, shared by the Metaverse and Connected System schemas so both
/// obey one rule.
/// </summary>
/// <remarks>
/// The Boolean rule (#2153): a multi-valued Boolean can only hold {TRUE}, {FALSE} or {TRUE, FALSE}. The first two are
/// one value and the third is a contradiction, so a Boolean attribute is always single-valued. No surface creates a
/// multi-valued Boolean Metaverse Attribute, and a Boolean a Connected System declares multi-valued is materialised as
/// single-valued.
/// </remarks>
public static class AttributeDataTypeCapabilities
{
    /// <summary>
    /// The rule, worded for an administrator, quoted by every surface that refuses a multi-valued Boolean.
    /// </summary>
    public const string MultiValuedBooleanRule =
        "A Boolean attribute holds one value, true or false, so it cannot be multi-valued.";

    /// <summary>
    /// Whether an attribute of this data type may be multi-valued. False for Boolean; true for every other type.
    /// </summary>
    public static bool SupportsMultiValued(AttributeDataType type) => type != AttributeDataType.Boolean;

    /// <summary>
    /// Whether the combination of data type and plurality is permitted.
    /// </summary>
    public static bool IsPermitted(AttributeDataType type, AttributePlurality plurality) =>
        plurality != AttributePlurality.MultiValued || SupportsMultiValued(type);

    /// <summary>
    /// The plurality an attribute of this data type holds: the requested plurality where the type permits it,
    /// otherwise single-valued. Used where JIM materialises a schema it does not author (a Connected System's).
    /// </summary>
    public static AttributePlurality Constrain(AttributeDataType type, AttributePlurality plurality) =>
        IsPermitted(type, plurality) ? plurality : AttributePlurality.SingleValued;
}
