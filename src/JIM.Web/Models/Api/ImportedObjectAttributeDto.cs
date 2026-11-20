// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Web.Models.Api;

/// <summary>One attribute of a rejected imported object, as the Connector returned it.</summary>
/// <remarks>#874.</remarks>
public class ImportedObjectAttributeDto
{
    /// <summary>The attribute's name, in the case the Connector returned it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>The attribute's type, as the Connector returned it. Serialised as the enum member name.</summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// The values recorded, as text, in the order the Connector returned them. A value over 1,000 characters is cut
    /// short and ends in "...", and a binary value is described rather than given, for example <c>[binary, 2,048 bytes]</c>.
    /// Empty when the values were withheld.
    /// </summary>
    public List<string> Values { get; set; } = [];

    /// <summary>How many of the attribute's values were not recorded (the first 50 are); null when every value was.</summary>
    public int? OmittedValueCount { get; set; }

    /// <summary>
    /// True when the attribute holds or may hold a credential, judged by its name, so its values were never recorded.
    /// </summary>
    public bool Withheld { get; set; }

    /// <summary>
    /// True when the item's error is about this attribute: an attribute the object returned more than once, one the
    /// Object Type's schema does not hold, or the External ID.
    /// </summary>
    public bool NamedInError { get; set; }

    /// <summary>Creates the DTO from the attribute recorded on the item.</summary>
    public static ImportedObjectAttributeDto FromModel(ImportedObjectSnapshotAttribute model) => new()
    {
        Name = model.Name,
        Type = model.Type,
        Values = model.Values,
        OmittedValueCount = model.OmittedValueCount,
        Withheld = model.Withheld == true,
        NamedInError = model.NamedInError == true
    };
}
