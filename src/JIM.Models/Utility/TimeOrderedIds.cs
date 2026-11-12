// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Models.Utility;

/// <summary>
/// The ids JIM gives the rows it writes itself: every row a raw SQL writer persists, and every row whose id is set in
/// code before it is written. They are all time-ordered (version 7). That covers JIM's largest tables: Connected System
/// Objects and their values, Metaverse Objects and their values, Pending Exports and their changes (#2929); the change
/// records of both kinds of object, with their attribute and value rows (#2669); and the Run Profile Execution Items a
/// run records, with their outcome and match candidate rows (#2807). The queued work and history an export writes in
/// bulk take theirs from here too: password and Exchange changes, Activity phases and causal edges.
/// </summary>
/// <remarks>
/// <para>
/// A version 7 id begins with the time it was made, so consecutive ids sort together and an insert lands on the
/// growing end of each index keyed on one: a table's primary key, and a child table's index on its parent's id. A
/// random (version 4) id lands on a random page of each instead. In the large tables those pages are rarely in memory,
/// so each insert read a page from disk and dirtied it: a page of 5,500 change attribute rows touched about as many
/// index pages per index, measured on PostgreSQL 18 at 9,700 pages written in full to the write-ahead log and 59 MB of
/// it for one batch into a four-million-row table, against about ten pages and 1.6 MB with version 7 ids, and a tenth of
/// the time (#2669). The Connected System Object value table gains less, because only two of the five indexes a text
/// value touches are keyed on an id (the rest on the value): copying 100,000 values in batches of 5,000 into a
/// 1.5-million-row table whose indexes were three times <c>shared_buffers</c> read and wrote about a third fewer
/// buffers, wrote a fifth or more less write-ahead log, and took 15 to 33% less time on one connection; on four
/// connections at once, where the batches meet at the growing end, the time varied either way between runs while the
/// buffers saved held (#2929, <c>TimeOrderedIdCopyBenchmarkDatabaseTests</c>).
/// </para>
/// <para>
/// Ids stay opaque: nothing may read a time, an order or a source from one, and keyset paging by id is still a total
/// order. Ids made within the same millisecond carry random bits after the timestamp, so they are not ordered among
/// themselves; only their neighbourhood in each index is.
/// </para>
/// <para>
/// A version 7 id gives away when it was made and is partly predictable, so a value that must be unguessable, such as a
/// credential or anything handed to a client as a capability, keeps a random id (<c>Guid.NewGuid()</c>), as does a value
/// that is never a row's id. <c>TimeOrderedIdConventionTests</c> holds every <c>Guid.NewGuid()</c> in the projects that
/// build rows to a <c>// random-id: &lt;reason&gt;</c> comment saying why.
/// </para>
/// <para>
/// Writers through EF Core need nothing from here for a key they leave unset: the PostgreSQL provider already generates
/// version 7 ids for those. This is for the raw SQL writers, which assign their own, and for code that sets an id before
/// a row is written.
/// </para>
/// </remarks>
public static class TimeOrderedIds
{
    /// <summary>
    /// A new time-ordered (version 7) id.
    /// </summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>
    /// Gives the object and each of its attribute values an id where it has none; an id already set is kept.
    /// </summary>
    public static void AssignMissing(ConnectedSystemObject connectedSystemObject)
    {
        if (connectedSystemObject.Id == Guid.Empty)
            connectedSystemObject.Id = New();

        foreach (var value in connectedSystemObject.AttributeValues.Where(v => v.Id == Guid.Empty))
            value.Id = New();
    }

    /// <summary>
    /// Gives the object and each of its attribute values an id where it has none; an id already set is kept.
    /// </summary>
    public static void AssignMissing(MetaverseObject metaverseObject)
    {
        if (metaverseObject.Id == Guid.Empty)
            metaverseObject.Id = New();

        foreach (var value in metaverseObject.AttributeValues.Where(v => v.Id == Guid.Empty))
            value.Id = New();
    }

    /// <summary>
    /// Gives the Pending Export and each of its attribute value changes an id where it has none; an id already set is
    /// kept.
    /// </summary>
    public static void AssignMissing(PendingExport pendingExport)
    {
        if (pendingExport.Id == Guid.Empty)
            pendingExport.Id = New();

        foreach (var change in pendingExport.AttributeValueChanges.Where(c => c.Id == Guid.Empty))
            change.Id = New();
    }

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
