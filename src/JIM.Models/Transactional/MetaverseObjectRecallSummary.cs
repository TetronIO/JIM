// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// Summary-tier projection of a referencing Metaverse Object for reference recall staging (#1003):
/// just enough to route the object to an export rule set (TypeId), evaluate rule scoping criteria
/// (ScopingAttributeValues), and label Activity reporting (DisplayName) - without materialising the
/// object's full attribute graph (a large group carries tens of thousands of member rows).
/// </summary>
public class MetaverseObjectRecallSummary
{
    public Guid Id { get; set; }

    /// <summary>
    /// The Metaverse Object Type id, used to select the export rules that apply to this object.
    /// </summary>
    public int TypeId { get; set; }

    /// <summary>
    /// The object's cached display name, used for Activity reporting snapshots.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Only the attribute values referenced by applicable export rules' scoping criteria (scalar
    /// columns plus the asserted-null marker; no navigations beyond the AttributeId scalar).
    /// Empty when no applicable rule carries scoping criteria.
    /// </summary>
    public List<MetaverseObjectAttributeValue> ScopingAttributeValues { get; } = [];
}
