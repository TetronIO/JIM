// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One auxiliary object class a discovery run saw in use, and how many entries of a given structural Object Type
/// carried it.
/// </summary>
/// <remarks>
/// The auxiliary class is recorded by name rather than as a foreign key to a Connected System Object Type. An entry
/// can carry an auxiliary class that the schema discovery never surfaced as an Object Type, and that case is the
/// most interesting thing a discovery run can report: it is a class in real use that JIM does not yet know about.
/// A foreign key would make that finding impossible to record.
/// </remarks>
public class AuxiliaryClassDiscoveryResult
{
    public int Id { get; set; }

    public AuxiliaryClassDiscoveryRun Run { get; set; } = null!;
    public int RunId { get; set; }

    /// <summary>
    /// The structural Object Type whose entries were read.
    /// </summary>
    public ConnectedSystemObjectType StructuralObjectType { get; set; } = null!;
    public int StructuralObjectTypeId { get; set; }

    /// <summary>
    /// The auxiliary class as the directory spells it.
    /// </summary>
    public string AuxiliaryClassName { get; set; } = null!;

    /// <summary>
    /// How many of the entries read carried this auxiliary class. Read against the run's
    /// <see cref="AuxiliaryClassDiscoveryRun.EntriesRead"/> and its scope: on a quick sample this is a count within
    /// the sample, not within the population.
    /// </summary>
    public int EntryCount { get; set; }

    public override string ToString()
    {
        return $"{AuxiliaryClassName} ({EntryCount})";
    }
}
