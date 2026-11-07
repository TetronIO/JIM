// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Models.Utility;

/// <summary>
/// The ids of change records: the Metaverse Object and Connected System Object change rows, their attribute rows and
/// their value rows. Every writer of those tables takes its ids from here, so they are all time-ordered (#2669). The
/// Run Profile Execution Items a run records, with their outcome and match candidate rows, take theirs from here too
/// (#2807): they are history written in bulk on every run, as change records are.
/// </summary>
/// <remarks>
/// <para>
/// A version 7 id begins with the time it was made, so consecutive ids sort together and an insert lands on the
/// growing end of each index keyed on one: a table's primary key, and a child table's index on its parent's id. A
/// random (version 4) id lands on a random page of each instead. In the change tables, by far JIM's largest, those
/// pages are rarely in memory, so each insert read a page from disk and dirtied it: a page of 5,500 attribute rows
/// touched about as many index pages per index, measured on PostgreSQL 18 at 9,700 pages written in full to the
/// write-ahead log and 59 MB of it for one batch into a four-million-row table, against about ten pages and 1.6 MB with
/// version 7 ids, and a tenth of the time.
/// </para>
/// <para>
/// Writers through EF Core need nothing from here: the PostgreSQL provider already generates version 7 ids for unset
/// Guid keys. This is for the raw SQL writers, which assign their own.
/// </para>
/// </remarks>
public static class ChangeRecordIds
{
    /// <summary>
    /// A new time-ordered (version 7) change record id.
    /// </summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>
    /// Gives the change, each of its attribute rows and each of their value rows an id where it has none; an id already
    /// set is kept.
    /// </summary>
    public static void AssignMissing(MetaverseObjectChange change)
    {
        if (change.Id == Guid.Empty)
            change.Id = New();

        AssignMissingToAttributes(change);
    }

    /// <summary>
    /// Gives each of the change's attribute rows, and each of their value rows, an id where it has none, leaving the
    /// change's own id alone: for attribute rows appended to a change already written.
    /// </summary>
    public static void AssignMissingToAttributes(MetaverseObjectChange change)
    {
        foreach (var attributeChange in change.AttributeChanges)
        {
            if (attributeChange.Id == Guid.Empty)
                attributeChange.Id = New();

            foreach (var valueChange in attributeChange.ValueChanges.Where(v => v.Id == Guid.Empty))
                valueChange.Id = New();
        }
    }

    /// <summary>
    /// Gives the change, each of its attribute rows and each of their value rows an id where it has none; an id already
    /// set is kept.
    /// </summary>
    public static void AssignMissing(ConnectedSystemObjectChange change)
    {
        if (change.Id == Guid.Empty)
            change.Id = New();

        foreach (var attributeChange in change.AttributeChanges)
        {
            if (attributeChange.Id == Guid.Empty)
                attributeChange.Id = New();

            foreach (var valueChange in attributeChange.ValueChanges.Where(v => v.Id == Guid.Empty))
                valueChange.Id = New();
        }
    }
}
