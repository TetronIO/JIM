// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Activities;

/// <summary>
/// One value an import found on an object but could not read as its attribute's type (#2066), as recorded on the
/// object's <see cref="ActivityRunProfileExecutionItem"/>. Stored with the item, so it is kept and deleted with the
/// Activity.
/// </summary>
public class ImportedAttributeValueError
{
    /// <summary>
    /// The attribute the value was found on.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// The attribute's type when the value was read, which is what the value failed to be; null where the attribute was
    /// not in the Connected System's schema.
    /// </summary>
    public AttributeDataType? AttributeType { get; set; }

    /// <summary>
    /// The value as the Connector received it, shortened if it was very long; null where the Connector could not render
    /// it as text.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Why the value could not be read.
    /// </summary>
    public string Reason { get; set; } = null!;

    /// <summary>
    /// Who found the value could not be imported: the Connector, which could not read it, or JIM, which could not hold
    /// it (#2153). Records written before this was kept read as the Connector, which is who raised every one of them.
    /// </summary>
    public ImportAttributeValueErrorSource Source { get; set; } = ImportAttributeValueErrorSource.Connector;
}
