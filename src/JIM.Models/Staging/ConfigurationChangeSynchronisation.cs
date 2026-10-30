// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A saved configuration change that a Connected System's next synchronisation must apply to every object of one
/// Connected System Object Type (#2615).
/// <para>
/// A Delta Synchronisation visits only the objects whose own data changed, so saving an import Synchronisation Rule or
/// an Object Matching Rule would otherwise reach existing objects only when a Full Synchronisation happened to run.
/// Each such save records one row per affected Connected System Object Type. The Connected System's next
/// synchronisation, Delta or Full, claims the rows awaiting synchronisation, processes every object of those types
/// (and only the modified objects of the others), and marks the rows applied once it completes. What runs is the
/// administrator's ordinary synchronisation; there is no separate kind of run.
/// </para>
/// <para>
/// Many changes awaiting synchronisation for one type mean the type is processed once, but each keeps its own row, so
/// the run can say which changes it applied.
/// </para>
/// <para>
/// The foreign keys carry no navigations, deliberately. Every reader already holds the Connected System, and a
/// navigation back to it (or to the Synchronisation Rule, whose own graph reaches the Connected System) would close a
/// cycle that the OpenAPI schema generator expands rather than collapses, exceeding its nesting limit and failing the
/// document build.
/// </para>
/// </summary>
public class ConfigurationChangeSynchronisation
{
    public long Id { get; set; }

    /// <summary>
    /// The Connected System whose next synchronisation applies the change. Cascade delete: the rows go with the system.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System Object Type whose objects the change can affect, and so whose every object the
    /// synchronisation processes. Cascade delete: a type that is removed from the schema has no objects left to process.
    /// </summary>
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// When the change was saved (UTC). Informational only: the claim never compares it with another clock, so a
    /// skewed host cannot make a run skip or repeat a change.
    /// </summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The Activity that recorded the saved change, i.e. why the type is awaiting synchronisation. Null once that
    /// Activity has been removed (history retention); <see cref="Reason"/> still reads.
    /// </summary>
    public Guid? ConfigurationChangeActivityId { get; set; }

    /// <summary>
    /// The Synchronisation Rule the change was made to, where it was made to one. Null for a change to an Object
    /// Matching Rule on a Connected System Object Type, and once the rule is deleted: the row is history, and
    /// outlives the rule it names (#1990).
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// A snapshot naming the change (the rule and what was done to it), so the row reads correctly after the rule is
    /// gone.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The synchronisation run that claimed the change to apply it. Overwritten when a run that claimed it fails or is
    /// cancelled and a later run claims it again. Null once that Activity has been removed.
    /// </summary>
    public Guid? ClaimedByActivityId { get; set; }

    /// <summary>
    /// When the claiming run completed and applied the change (UTC). Null means the change is awaiting synchronisation.
    /// </summary>
    public DateTime? AppliedAt { get; set; }

    public override string ToString()
    {
        return $"{nameof(ConfigurationChangeSynchronisation)}: Connected System {ConnectedSystemId}, " +
               $"Object Type {ConnectedSystemObjectTypeId} ({(AppliedAt.HasValue ? "applied" : "awaiting synchronisation")})";
    }
}
