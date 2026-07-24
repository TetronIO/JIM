// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Proves the raw-SQL bulk insert column lists (COPY binary import and chunked INSERT, used to
/// persist newly projected Metaverse Objects and their attribute values) cover every column the
/// EF Core model maps for those tables. Raw SQL bypasses the EF model, so a migration that adds a
/// column leaves these inserts silently defaulting it for every bulk-written row; that is how
/// newly projected Metaverse Object attribute values lost their ContributedBySyncRuleId provenance
/// and NullValue markers (#91), which in turn let a lower-priority contributor overwrite a
/// higher-priority value because the incumbent lookup found no owning Synchronisation Rule.
/// These tests turn that failure mode into a unit test failure at the point the column is added.
/// </summary>
public class BulkInsertColumnCompletenessTests
{
    private IModel _model = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Building the model needs the Npgsql provider for relational metadata but never connects;
        // the connection string is a syntactically valid placeholder.
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only;Password=model-only")
            .Options;
        using var context = new JimDbContext(options);
        _model = context.Model;
    }

    [Test]
    public void MetaverseObjectBulkInsertColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(MetaverseObject), "MetaverseObjects", MvoBulkInsertColumns.MetaverseObjects);
    }

    [Test]
    public void MetaverseObjectAttributeValueBulkInsertColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(MetaverseObjectAttributeValue), "MetaverseObjectAttributeValues", MvoBulkInsertColumns.MetaverseObjectAttributeValues);
    }

    /// <summary>
    /// The raw-SQL update path (<c>SyncRepository.UpdateMetaverseObjectsBulkAsync</c>) writes the mutable subset
    /// of the insert columns: everything except the immutable primary key (Id) and the create-only Created
    /// timestamp. A migration that adds a mutable Metaverse Object column must extend both lists (and the update
    /// writer) or the raw update would silently never persist it.
    /// </summary>
    [Test]
    public void MetaverseObjectBulkUpdateColumns_AreTheMutableSubsetOfInsertColumns()
    {
        var expected = MvoBulkInsertColumns.MetaverseObjects
            .Where(c => c is not "Id" and not "Created")
            .ToHashSet();
        var actual = MvoBulkInsertColumns.MetaverseObjectsUpdate.ToHashSet();

        var missing = expected.Except(actual).OrderBy(c => c).ToList();
        var unknown = actual.Except(expected).OrderBy(c => c).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty,
                "Mutable column(s) in the insert list are missing from MetaverseObjectsUpdate; the raw-SQL update " +
                "would never persist them. Extend MvoBulkInsertColumns.MetaverseObjectsUpdate AND the SET clause / " +
                "VALUES writer in BulkUpdateMvoRowsViaEfAsync (in list order): " + string.Join(", ", missing));
            Assert.That(unknown, Is.Empty,
                "MetaverseObjectsUpdate contains column(s) not in the insert list (or the immutable Id/Created): " +
                string.Join(", ", unknown));
        });
    }

    [Test]
    public void MetaverseObjectChangeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(MetaverseObjectChange), "MetaverseObjectChanges", MvoChangeBulkColumns.MetaverseObjectChanges);
    }

    [Test]
    public void MetaverseObjectChangeAttributeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(MetaverseObjectChangeAttribute), "MetaverseObjectChangeAttributes", MvoChangeBulkColumns.MetaverseObjectChangeAttributes);
    }

    [Test]
    public void MetaverseObjectChangeAttributeValueBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(MetaverseObjectChangeAttributeValue), "MetaverseObjectChangeAttributeValues", MvoChangeBulkColumns.MetaverseObjectChangeAttributeValues);
    }

    [Test]
    public void ConnectedSystemObjectBulkInsertColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Staging.ConnectedSystemObject), "ConnectedSystemObjects", CsoBulkColumns.ConnectedSystemObjects);
    }

    [Test]
    public void ConnectedSystemObjectAttributeValueBulkInsertColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Staging.ConnectedSystemObjectAttributeValue), "ConnectedSystemObjectAttributeValues", CsoBulkColumns.ConnectedSystemObjectAttributeValues);
    }

    /// <summary>
    /// The CSO bulk update writes the mutable subset of the insert columns. The exclusions are an
    /// explicit list (immutable identity/creation columns, plus the scope-evaluation columns that
    /// have their own dedicated persistence path; see CsoBulkColumns for the rationale), so a
    /// migration that adds a mutable Connected System Object column must consciously place it in
    /// either the update list or the exclusion list; silence fails this test.
    /// </summary>
    [Test]
    public void ConnectedSystemObjectBulkUpdateColumns_AreTheMutableSubsetOfInsertColumns()
    {
        var expected = CsoBulkColumns.ConnectedSystemObjects
            .Except(CsoBulkColumns.ConnectedSystemObjectsUpdateExclusions)
            .ToHashSet();
        var actual = CsoBulkColumns.ConnectedSystemObjectsUpdate.ToHashSet();

        var missing = expected.Except(actual).OrderBy(c => c).ToList();
        var unknown = actual.Except(expected).OrderBy(c => c).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty,
                "Mutable column(s) in the insert list are in neither ConnectedSystemObjectsUpdate nor the " +
                "documented exclusion list; the raw bulk update would silently never persist them. Add each to " +
                "one of the two lists (and the cast/parameter writers in BulkUpdateConnectedSystemObjectsRawAsync " +
                "if updatable): " + string.Join(", ", missing));
            Assert.That(unknown, Is.Empty,
                "ConnectedSystemObjectsUpdate contains column(s) not in the insert list (or listed as excluded): " +
                string.Join(", ", unknown));
        });
    }

    [Test]
    public void ConnectedSystemObjectChangeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Staging.ConnectedSystemObjectChange), "ConnectedSystemObjectChanges", CsoChangeBulkColumns.ConnectedSystemObjectChanges);
    }

    [Test]
    public void ConnectedSystemObjectChangeAttributeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Staging.ConnectedSystemObjectChangeAttribute), "ConnectedSystemObjectChangeAttributes", CsoChangeBulkColumns.ConnectedSystemObjectChangeAttributes);
    }

    [Test]
    public void ConnectedSystemObjectChangeAttributeValueBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Staging.ConnectedSystemObjectChangeAttributeValue), "ConnectedSystemObjectChangeAttributeValues", CsoChangeBulkColumns.ConnectedSystemObjectChangeAttributeValues);
    }

    [Test]
    public void ActivityRunProfileExecutionItemBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Activities.ActivityRunProfileExecutionItem), "ActivityRunProfileExecutionItems", RpeiBulkColumns.ActivityRunProfileExecutionItems);
    }

    [Test]
    public void ActivityRunProfileExecutionItemSyncOutcomeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Activities.ActivityRunProfileExecutionItemSyncOutcome), "ActivityRunProfileExecutionItemSyncOutcomes", RpeiBulkColumns.ActivityRunProfileExecutionItemSyncOutcomes);
    }

    [Test]
    public void ActivityRunProfileExecutionItemUpdateColumns_HaveAConsciousHomeForEveryColumn()
    {
        AssertUpdateListsCoverInsertList(
            "ActivityRunProfileExecutionItems",
            RpeiBulkColumns.ActivityRunProfileExecutionItems,
            [RpeiBulkColumns.ActivityRunProfileExecutionItemsUpdate],
            RpeiBulkColumns.ActivityRunProfileExecutionItemsUpdateExclusions);
    }

    [Test]
    public void PendingExportBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Transactional.PendingExport), "PendingExports", PendingExportBulkColumns.PendingExports);
    }

    [Test]
    public void PendingExportAttributeValueChangeBulkColumns_MatchMappedColumnsExactly()
    {
        AssertColumnListMatchesModel(typeof(JIM.Models.Transactional.PendingExportAttributeValueChange), "PendingExportAttributeValueChanges", PendingExportBulkColumns.PendingExportAttributeValueChanges);
    }

    [Test]
    public void PendingExportUpdateColumns_HaveAConsciousHomeForEveryColumn()
    {
        AssertUpdateListsCoverInsertList(
            "PendingExports",
            PendingExportBulkColumns.PendingExports,
            [PendingExportBulkColumns.PendingExportsRetryUpdate, PendingExportBulkColumns.PendingExportsExportResultUpdate],
            PendingExportBulkColumns.PendingExportsUpdateExclusions);
    }

    [Test]
    public void PendingExportAttributeValueChangeUpdateColumns_HaveAConsciousHomeForEveryColumn()
    {
        AssertUpdateListsCoverInsertList(
            "PendingExportAttributeValueChanges",
            PendingExportBulkColumns.PendingExportAttributeValueChanges,
            [PendingExportBulkColumns.PendingExportAttributeValueChangesConfirmationUpdate, PendingExportBulkColumns.PendingExportAttributeValueChangesExportResultUpdate],
            PendingExportBulkColumns.PendingExportAttributeValueChangesUpdateExclusions);
    }

    /// <summary>
    /// Asserts every insert column appears in at least one update list or the documented exclusion
    /// list, and that no update/exclusion column is unknown to the insert list. A table can have
    /// more than one legitimate update shape (for example retry reconciliation vs export result);
    /// this keeps every column consciously placed whichever shape a migration extends.
    /// </summary>
    private static void AssertUpdateListsCoverInsertList(string tableName, string[] insertColumns, string[][] updateLists, string[] exclusions)
    {
        var insert = insertColumns.ToHashSet();
        var covered = updateLists.SelectMany(l => l).Concat(exclusions).ToHashSet();

        var missing = insert.Except(covered).OrderBy(c => c).ToList();
        var unknown = covered.Except(insert).OrderBy(c => c).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty,
                $"{tableName}: column(s) are in neither an update list nor the documented exclusion list; a raw " +
                "update would silently never persist them. Place each consciously (and extend the matching " +
                "statement's writers if updatable): " + string.Join(", ", missing));
            Assert.That(unknown, Is.Empty,
                $"{tableName}: update/exclusion list(s) contain column(s) not in the insert list: " +
                string.Join(", ", unknown));
        });
    }

    private void AssertColumnListMatchesModel(Type entityClrType, string tableName, string[] bulkInsertColumns)
    {
        var entityType = _model.FindEntityType(entityClrType);
        Assert.That(entityType, Is.Not.Null, $"Entity type {entityClrType.Name} not found in the EF model.");

        var table = StoreObjectIdentifier.Table(tableName, null);
        var mappedColumns = entityType!.GetProperties()
            // Store-generated-on-add-or-update columns (the xmin concurrency token) are assigned by
            // PostgreSQL and must not appear in an insert column list.
            .Where(p => p.ValueGenerated != ValueGenerated.OnAddOrUpdate)
            .Select(p => p.GetColumnName(table))
            .Where(c => c != null)
            .Cast<string>()
            .ToHashSet();

        var bulkColumns = bulkInsertColumns.ToHashSet();

        var missingFromBulkInsert = mappedColumns.Except(bulkColumns).OrderBy(c => c).ToList();
        var unknownInBulkInsert = bulkColumns.Except(mappedColumns).OrderBy(c => c).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missingFromBulkInsert, Is.Empty,
                $"{tableName}: mapped column(s) missing from the bulk insert list; every bulk-written row would " +
                $"silently default them. Extend MvoBulkInsertColumns.{tableName} AND the corresponding COPY/INSERT " +
                $"writers in SyncRepository.MvoOperations.cs (values must be written in list order): " +
                string.Join(", ", missingFromBulkInsert));
            Assert.That(unknownInBulkInsert, Is.Empty,
                $"{tableName}: bulk insert list contains column(s) the EF model no longer maps: " +
                string.Join(", ", unknownInBulkInsert));
        });
    }
}
