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
