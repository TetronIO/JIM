// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One attempt to find out which auxiliary object classes the entries in a Connected System actually use.
/// </summary>
/// <remarks>
/// The results are evidence, never configuration: nothing here changes the schema, and an administrator's
/// selections live in <see cref="ConnectedSystemObjectTypeExtension"/>. Discovery only makes the decision an
/// informed one.
/// <para>
/// A Connected System keeps its runs rather than one current answer, so an administrator can see that the last
/// look was a quick sample taken months ago rather than a full scan taken this morning; a count with no provenance
/// invites more confidence than it has earned. At most one run per Connected System may be
/// <see cref="AuxiliaryClassDiscoveryStatus.InProgress"/> at a time.
/// </para>
/// </remarks>
public class AuxiliaryClassDiscoveryRun
{
    public int Id { get; set; }

    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    public AuxiliaryClassDiscoveryScope Scope { get; set; } = AuxiliaryClassDiscoveryScope.NotSet;

    /// <summary>
    /// How many entries per structural Object Type a <see cref="AuxiliaryClassDiscoveryScope.QuickSample"/> read.
    /// Null for a full scan, where there is no per-type limit.
    /// </summary>
    public int? SampleSizePerObjectType { get; set; }

    public AuxiliaryClassDiscoveryStatus Status { get; set; } = AuxiliaryClassDiscoveryStatus.NotSet;

    public DateTime Started { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the run stopped, however it stopped. Null while it is still going.
    /// </summary>
    public DateTime? Completed { get; set; }

    /// <summary>
    /// How many entries were read. Meaningful on a cancelled or failed run too, as it says how much of the
    /// population the partial results actually cover.
    /// </summary>
    public int EntriesRead { get; set; }

    /// <summary>
    /// The Activity recording this run, so an administrator reaches the same progress, cancellation and error
    /// reporting every other long-running operation in JIM offers. Nullable because the run row and its Activity
    /// are created in the same unit of work.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// Who asked for the run, denormalised in the same shape an Activity records its initiator, so the run stays
    /// readable after the principal is gone.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    public string? InitiatedByName { get; set; }

    /// <summary>
    /// Why the run failed, when <see cref="Status"/> is <see cref="AuxiliaryClassDiscoveryStatus.Failed"/>.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The auxiliary classes this run found, one row per structural Object Type and auxiliary class pairing.
    /// </summary>
    public List<AuxiliaryClassDiscoveryResult> Results { get; set; } = new();

    public override string ToString()
    {
        return $"{Scope} ({Status}), {EntriesRead} entries read";
    }
}
