// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.TestSupport;
using NUnit.Framework;

namespace JIM.Worker.Tests.Support;

/// <summary>
/// The comparison half of the preview isolation assertion (#288, PRD requirement 10): given two snapshots of the
/// synchronisation-integrity tables, say precisely what changed. Pure logic, so it is tested here without a
/// database; whether a capture reflects reality is <see cref="DatabaseIsolationSnapshotDatabaseTests"/>' job.
/// </summary>
[TestFixture]
public class DatabaseIsolationSnapshotTests
{
    [Test]
    public void Diff_IdenticalSnapshots_ReportsNothing()
    {
        var before = Snapshot(("PendingExports", 3, "abc"), ("Activities", 10, "def"));
        var after = Snapshot(("PendingExports", 3, "abc"), ("Activities", 10, "def"));

        Assert.That(DatabaseIsolationSnapshot.Diff(before, after), Is.Empty);
    }

    [Test]
    public void Diff_RowCountChanged_NamesTheTableAndBothCounts()
    {
        // The count is the actionable part of the message: "PendingExports: 3 -> 4 rows" tells the reader what
        // leaked; a bare "changed" sends them off to diff the database by hand.
        var before = Snapshot(("PendingExports", 3, "abc"));
        var after = Snapshot(("PendingExports", 4, "zzz"));

        var diff = DatabaseIsolationSnapshot.Diff(before, after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff, Has.Count.EqualTo(1));
            Assert.That(diff[0], Does.Contain("PendingExports"));
            Assert.That(diff[0], Does.Contain("3"));
            Assert.That(diff[0], Does.Contain("4"));
        }
    }

    [Test]
    public void Diff_ContentChangedWithSameRowCount_SaysSoRatherThanClaimingACountChange()
    {
        // An in-place UPDATE leaves the count identical, and a snapshot that only counted rows would pass. The
        // digest exists for exactly this case, and the message has to distinguish it from an insert or delete.
        var before = Snapshot(("MetaverseObjectAttributeValues", 5, "abc"));
        var after = Snapshot(("MetaverseObjectAttributeValues", 5, "different"));

        var diff = DatabaseIsolationSnapshot.Diff(before, after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(diff, Has.Count.EqualTo(1));
            Assert.That(diff[0], Does.Contain("MetaverseObjectAttributeValues"));
            Assert.That(diff[0], Does.Contain("content changed"));
        }
    }

    [Test]
    public void Diff_SnapshotsOverDifferentTableSets_Throws()
    {
        // Two snapshots that watched different tables cannot be compared honestly; a silent intersection would
        // report "unchanged" about a table only one of them ever looked at.
        var before = Snapshot(("PendingExports", 3, "abc"));
        var after = Snapshot(("Activities", 3, "abc"));

        Assert.That(() => DatabaseIsolationSnapshot.Diff(before, after),
            Throws.ArgumentException.With.Message.Contain("table"));
    }

    [Test]
    public void AssertUnchangedSince_SomethingChanged_FailsNamingEveryChangedTable()
    {
        var before = Snapshot(("PendingExports", 0, "empty"), ("Activities", 2, "abc"));
        var after = Snapshot(("PendingExports", 1, "aaa"), ("Activities", 2, "changed"));

        Assert.That(() => after.AssertUnchangedSince(before),
            Throws.InstanceOf<AssertionException>()
                .With.Message.Contain("PendingExports").And.Message.Contain("Activities"));
    }

    [Test]
    public void SyncIntegrityTables_CoverThePopulationsThePrdNames()
    {
        // PRD requirement 10 names Pending Exports, Metaverse Objects and their attribute values, Connected
        // System Objects, RPEIs and Activities. This pins the watched set so a rename or a trim fails a test
        // rather than silently weakening every isolation assertion in the suite.
        Assert.That(DatabaseIsolationSnapshot.SyncIntegrityTables, Is.EquivalentTo(new[]
        {
            "PendingExports",
            "PendingExportAttributeValueChanges",
            "MetaverseObjects",
            "MetaverseObjectAttributeValues",
            "ConnectedSystemObjects",
            "ConnectedSystemObjectAttributeValues",
            "ActivityRunProfileExecutionItems",
            "Activities"
        }));
    }

    private static DatabaseIsolationSnapshot Snapshot(params (string Table, long Count, string Digest)[] tables) =>
        new(tables.ToDictionary(t => t.Table, t => new DatabaseTableState(t.Count, t.Digest)));
}
