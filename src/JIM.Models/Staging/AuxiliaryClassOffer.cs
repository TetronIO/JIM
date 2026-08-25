// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One auxiliary class an administrator may merge into a structural Connected System Object Type, with everything
/// the decision needs: whether it is merged already, what it would contribute, and why JIM thinks it is relevant.
/// </summary>
/// <remarks>
/// The suggestions are never configuration. A class is offered whether or not anything suggests it, and only what
/// an administrator merges is persisted, so a schema refresh cannot silently change what a Connected System Object
/// Type carries.
/// </remarks>
public class AuxiliaryClassOffer
{
    /// <summary>
    /// The auxiliary class, as its own Connected System Object Type.
    /// </summary>
    public ConnectedSystemObjectType ObjectType { get; init; } = null!;

    /// <summary>
    /// Whether an administrator has merged this class into the Object Type the offer was built for.
    /// </summary>
    public bool Merged { get; init; }

    /// <summary>
    /// How many attributes merging this class would contribute.
    /// </summary>
    public int ContributedAttributeCount { get; init; }

    /// <summary>
    /// Whether the Connected System itself says this class may attach to the Object Type, i.e. an RFC 4512 DIT
    /// Content Rule names it. Most directories publish no such statement, so its absence says nothing.
    /// </summary>
    public bool PermittedByTheConnectedSystem { get; init; }

    /// <summary>
    /// How many of the entries the last discovery run read were carrying this class, or null when no run has
    /// observed it. Read against the run's own scope: a quick sample counts within the sample, not the population.
    /// </summary>
    public int? EntriesObservedOn { get; init; }

    /// <summary>
    /// Whether anything suggests this class, as opposed to it merely being defined in the Connected System's schema.
    /// </summary>
    public bool IsSuggested => PermittedByTheConnectedSystem || EntriesObservedOn > 0;

    public override string ToString()
    {
        return $"{ObjectType.Name} ({(Merged ? "merged" : "available")})";
    }
}
