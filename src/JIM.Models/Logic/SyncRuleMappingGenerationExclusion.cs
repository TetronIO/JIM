// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
namespace JIM.Models.Logic;

/// <summary>
/// A Connected System excluded from a generated mapping's availability checks (Unique Value Generation, #242):
/// the uniqueness gates never reserve against, or check, this system's connector space when proposing a value for
/// this flow. Composite primary key on (<see cref="SyncRuleMappingGenerationId"/>, <see cref="ConnectedSystemId"/>),
/// so a system can be excluded at most once per flow. Whether an excluded system actually participates (has an
/// export Attribute Flow targeting the attribute) is validated by the application layer, not here.
/// </summary>
public class SyncRuleMappingGenerationExclusion
{
    public int SyncRuleMappingGenerationId { get; set; }

    public SyncRuleMappingGeneration? Generation { get; set; }

    public int ConnectedSystemId { get; set; }

    public ConnectedSystem? ConnectedSystem { get; set; }
}
