// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Web.Models.Api;

/// <summary>
/// A proposed schema selection for a Connected System, submitted to find out what it would do before making it
/// (#827 gap G6, #1475): which Object Types JIM would manage, which of their attributes it would import, and
/// whether obsoleting an object of each type would withdraw the Metaverse values it contributed.
/// </summary>
/// <remarks>
/// Every field is optional and every omission means "leave this as it stands", at both levels: an Object Type the
/// request does not name is left alone entirely, and a field it does not set on a Type it does name keeps that
/// Type's stored value. Silence is never read as a default, because the defaults here are destructive: read as
/// <c>false</c>, an omitted <c>selected</c> would preview taking a whole Object Type out of management on a
/// request that only meant to change one attribute.
///
/// The natural way to use it is to read the current schema from <c>GET connected-systems/{id}</c>, apply the
/// intended changes, preview the result, and then make the changes through the schema update endpoints.
/// </remarks>
public class StartConnectedSystemSchemaPreviewRequest
{
    /// <summary>
    /// The Object Types whose selection would change. Omitted or null previews the schema exactly as it stands,
    /// which answers "what does the configuration already in force do?" rather than being refused.
    /// </summary>
    public List<ConnectedSystemObjectTypeSelectionRequest>? ObjectTypes { get; set; }

    /// <summary>
    /// Whether every drill-down row is kept, or only the per-group cap's worth. Capped by default, which is the
    /// right answer for all but the largest previews. Group counts are exact either way; this decides only how much
    /// of the detail behind them can be read back.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;
}

/// <summary>
/// One Object Type's proposed selection within a schema preview request.
/// </summary>
public class ConnectedSystemObjectTypeSelectionRequest
{
    /// <summary>
    /// The Connected System Object Type this proposal is for. Required; an id naming no Object Type on this
    /// Connected System is refused rather than ignored.
    /// </summary>
    public int ObjectTypeId { get; set; }

    /// <summary>
    /// Whether JIM would manage this Object Type. Omitted keeps its stored selection.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Whether obsoleting one of this Object Type's objects would withdraw the Metaverse attribute values that
    /// object contributed. Omitted keeps the stored setting.
    /// </summary>
    public bool? RemoveContributedAttributesOnObsoletion { get; set; }

    /// <summary>
    /// The attributes JIM would import for this Object Type. Omitted keeps the stored selection; an empty list
    /// previews deselecting every attribute, which is a different statement and is honoured.
    ///
    /// Send the whole set for the Type rather than the attributes that changed: what a deselection costs is a
    /// property of the resulting selection, not of the flag that moved. External IDs are selected implicitly and
    /// need not be listed; deselecting one is refused with a blocking finding.
    /// </summary>
    public List<int>? SelectedAttributeIds { get; set; }
}
