// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The Connected System Object an External ID value names, as a summary: what deletion detection needs of a candidate to
/// decide it (#2521). Where more than one object holds the value, this is the first of them by id, which is the object
/// the single-value lookup by attribute returns.
/// </summary>
/// <param name="Id">The object's id.</param>
/// <param name="Status">The object's status when it was looked up.</param>
/// <param name="TypeId">The object's Object Type.</param>
/// <param name="MatchCount">How many objects hold the value; more than one means duplicate objects to investigate.</param>
public sealed record ConnectedSystemObjectExternalIdMatch(Guid Id, ConnectedSystemObjectStatus Status, int TypeId, int MatchCount);
