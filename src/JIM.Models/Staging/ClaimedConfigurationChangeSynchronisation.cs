// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A <see cref="ConfigurationChangeSynchronisation"/> a synchronisation run has claimed: just enough for the run to
/// know which Connected System Object Types to process in full and how many changes are behind each.
/// </summary>
/// <param name="Id">The claimed row's id.</param>
/// <param name="ConnectedSystemObjectTypeId">The Connected System Object Type whose every object the run processes.</param>
public readonly record struct ClaimedConfigurationChangeSynchronisation(long Id, int ConnectedSystemObjectTypeId);
