// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic;

/// <summary>
/// One Metaverse-Derived Attribute Flow (#1750): an import mapping whose expression (or, for a generated mapping,
/// whose base expression) reads at least one <c>mv["..."]</c> attribute of the object being flowed, resolved against
/// its Metaverse Object Type.
/// </summary>
/// <param name="Mapping">The derived import mapping.</param>
/// <param name="SyncRule">The Synchronisation Rule hosting it.</param>
/// <param name="MetaverseObjectTypeId">The Metaverse Object Type the hosting rule flows to.</param>
/// <param name="TargetAttributeId">The Metaverse attribute the mapping writes.</param>
/// <param name="TargetAttributeName">The target attribute's name.</param>
/// <param name="TargetAttributeType">The target attribute's data type.</param>
/// <param name="Inputs">The Metaverse attributes the expression reads, in the order it first mentions them; names
/// that do not resolve to an attribute of the type are absent here and reported as unknown inputs instead.</param>
public sealed record DerivedFlow(
    SyncRuleMapping Mapping,
    SyncRule SyncRule,
    int MetaverseObjectTypeId,
    int TargetAttributeId,
    string TargetAttributeName,
    AttributeDataType TargetAttributeType,
    IReadOnlyList<MetaverseAttribute> Inputs);
