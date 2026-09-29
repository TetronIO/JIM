// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// An <c>mv["..."]</c> name in a derived import mapping's expression that is not an attribute of the hosting rule's
/// Metaverse Object Type (#1750). Recorded rather than treated as a dependency, and rejected at save.
/// </summary>
/// <param name="Mapping">The mapping whose expression reads the name.</param>
/// <param name="SyncRule">The Synchronisation Rule hosting it.</param>
/// <param name="MetaverseObjectTypeId">The rule's Metaverse Object Type.</param>
/// <param name="MetaverseObjectTypeName">The type's name, for the rejection message.</param>
/// <param name="AttributeName">The name exactly as written in the expression.</param>
public sealed record DerivedFlowUnknownInput(
    SyncRuleMapping Mapping,
    SyncRule SyncRule,
    int MetaverseObjectTypeId,
    string MetaverseObjectTypeName,
    string AttributeName);
