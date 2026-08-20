// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Structural guard for the migration Designer chain. Every migration ships a Designer holding a
/// point-in-time snapshot of the whole model, and <c>dotnet ef migrations remove</c> restores
/// JimDbContextModelSnapshot.cs from the *previous* migration's Designer. A Designer scaffolded
/// against a stale snapshot (the usual cause: a feature branch that gained its migration before
/// someone else's landed on main, and was merged without regenerating) therefore corrupts the model
/// snapshot on the next routine add-then-remove, and nothing fails while it happens: the snapshot is
/// simply wrong, and the next migration generated from it tries to re-add a column the database
/// already has. See issue #1379.
///
/// The guard is that each Designer must differ from its predecessor by exactly what its own
/// migration does, and that the newest Designer must match the model snapshot. Both comparisons use
/// EF Core's own relational model, so they see the tables, columns, keys, indexes and foreign keys
/// the provider would actually create rather than the C# in the file.
/// </summary>
public class MigrationDesignerChainTests
{
    /// <summary>
    /// The oldest migration whose Designer is trusted. Designers before this point drifted over
    /// roughly two months of parallel branch merges and are left as-is: they are historical records
    /// only, and repairing them means hand-editing 40-odd snapshots (including whole entity blocks
    /// with their relationships) for no runtime benefit. What matters is that the drift stops here,
    /// so an add-then-remove anywhere in current history is safe. Never move this forward to silence
    /// a failure: a new migration's Designer failing this test is the bug the test exists to catch.
    /// </summary>
    private const string FirstVerifiedMigration = "20260810105636_AddConnectedSystemContainerExcluded";

    [Test]
    public void MigrationDesigners_ComparedWithTheirPredecessor_DifferOnlyByTheirOwnOperations()
    {
        using var context = new JimDbContextFactory().CreateDbContext([]);
        var migrations = context.GetService<IMigrationsAssembly>();
        var provider = context.GetService<IDatabaseProvider>().Name;
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var verifiedFrom = migrations.Migrations.Keys.ToList().IndexOf(FirstVerifiedMigration);
        Assert.That(verifiedFrom, Is.GreaterThanOrEqualTo(0),
            $"{nameof(FirstVerifiedMigration)} names '{FirstVerifiedMigration}', which is not a migration in this assembly.");

        var failures = new List<string>();
        Dictionary<string, ShapeEntry>? previousShape = null;
        var previousId = string.Empty;
        var index = 0;

        foreach (var (id, typeInfo) in migrations.Migrations)
        {
            var migration = migrations.CreateMigration(typeInfo, provider);
            var shape = Describe(migration.TargetModel, initializer);

            // The pair ending at FirstVerifiedMigration straddles the historical drift, so checking
            // starts with the pair after it.
            if (previousShape != null && index > verifiedFrom)
            {
                var unexplained = Unexplained(previousShape, shape, migration.UpOperations).ToList();
                if (unexplained.Count > 0)
                    failures.Add($"{id} (predecessor {previousId}):{Environment.NewLine}  " +
                                 string.Join($"{Environment.NewLine}  ", unexplained));
            }

            previousShape = shape;
            previousId = id;
            index++;
        }

        Assert.That(failures, Is.Empty, () =>
            "The Designers below differ from their predecessor by more than their own migration does, so " +
            "'dotnet ef migrations remove' at that point in history would corrupt the model snapshot. Each was " +
            "almost certainly scaffolded against a stale snapshot: regenerate it, or hand-correct the entity " +
            "block to match. Do not move " + nameof(FirstVerifiedMigration) + " to make this pass." +
            Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, failures));
    }

    [Test]
    public void ModelSnapshot_ComparedWithTheNewestMigrationDesigner_IsIdentical()
    {
        using var context = new JimDbContextFactory().CreateDbContext([]);
        var migrations = context.GetService<IMigrationsAssembly>();
        var provider = context.GetService<IDatabaseProvider>().Name;
        var initializer = context.GetService<IModelRuntimeInitializer>();

        var newest = migrations.Migrations.Last();
        var newestShape = Describe(migrations.CreateMigration(newest.Value, provider).TargetModel, initializer);

        var snapshotModel = migrations.ModelSnapshot?.Model;
        Assert.That(snapshotModel, Is.Not.Null, "JimDbContextModelSnapshot could not be loaded from the migrations assembly.");

        var differences = Unexplained(newestShape, Describe(snapshotModel!, initializer), []).ToList();
        Assert.That(differences, Is.Empty, () =>
            $"JimDbContextModelSnapshot does not match the newest migration's Designer ({newest.Key}), so one of " +
            "the two was not regenerated. Differences, reading from the Designer to the snapshot:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", differences));
    }

    /// <summary>
    /// One addressable part of a model: what it belongs to, and the shape it must keep. Table names
    /// are unqualified because JIM maps everything into the default schema.
    /// </summary>
    private sealed record ShapeEntry(string Table, string Value);

    /// <summary>
    /// Flattens a model into comparable entries covering the tables, columns, primary keys, unique
    /// constraints, indexes and foreign keys the provider would create.
    /// </summary>
    private static Dictionary<string, ShapeEntry> Describe(IModel model, IModelRuntimeInitializer initializer)
    {
        // A Designer's BuildTargetModel hands back a model that has been neither finalised nor given
        // its runtime annotations, and GetRelationalModel needs both.
        if (model is IMutableModel mutableModel)
            model = mutableModel.FinalizeModel();
        model = initializer.Initialize(model, designTime: true, validationLogger: null);

        var shape = new Dictionary<string, ShapeEntry>(StringComparer.Ordinal);
        foreach (var table in model.GetRelationalModel().Tables)
        {
            void Add(string key, string value) => shape[key] = new ShapeEntry(table.Name, value);

            Add($"table:{table.Name}", string.Empty);

            foreach (var column in table.Columns)
                Add($"column:{table.Name}.{column.Name}", $"{column.StoreType} {(column.IsNullable ? "NULL" : "NOT NULL")}");

            if (table.PrimaryKey != null)
                Add($"primary key:{table.Name}.{table.PrimaryKey.Name}", Columns(table.PrimaryKey.Columns));

            foreach (var unique in table.UniqueConstraints.Where(u => u.Name != table.PrimaryKey?.Name))
                Add($"unique constraint:{table.Name}.{unique.Name}", Columns(unique.Columns));

            foreach (var index in table.Indexes)
                Add($"index:{table.Name}.{index.Name}", $"{Columns(index.Columns)} unique={index.IsUnique} filter={index.Filter}");

            foreach (var foreignKey in table.ForeignKeyConstraints)
                Add($"foreign key:{table.Name}.{foreignKey.Name}",
                    $"{Columns(foreignKey.Columns)} -> {foreignKey.PrincipalTable.Name}({Columns(foreignKey.PrincipalColumns)}) " +
                    $"on delete {foreignKey.OnDeleteAction}");
        }

        return shape;
    }

    private static string Columns(IEnumerable<IColumn> columns) => string.Join(",", columns.Select(c => c.Name));

    /// <summary>
    /// Returns the differences between two model shapes that the supplied operations do not account
    /// for. Tables a migration creates, drops or renames are wildcards: the one operation explains
    /// everything they contain.
    /// </summary>
    private static IEnumerable<string> Unexplained(
        Dictionary<string, ShapeEntry> before,
        Dictionary<string, ShapeEntry> after,
        IReadOnlyList<MigrationOperation> operations)
    {
        var explained = new HashSet<string>(StringComparer.Ordinal);
        var wholeTables = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation create:
                    wholeTables.Add(create.Name);
                    break;
                case DropTableOperation drop:
                    wholeTables.Add(drop.Name);
                    break;
                case RenameTableOperation rename:
                    wholeTables.Add(rename.Name!);
                    wholeTables.Add(rename.NewName!);
                    break;
                case AddColumnOperation add:
                    explained.Add($"column:{add.Table}.{add.Name}");
                    break;
                case DropColumnOperation drop:
                    explained.Add($"column:{drop.Table}.{drop.Name}");
                    break;
                case AlterColumnOperation alter:
                    explained.Add($"column:{alter.Table}.{alter.Name}");
                    break;
                case RenameColumnOperation rename:
                    explained.Add($"column:{rename.Table}.{rename.Name}");
                    explained.Add($"column:{rename.Table}.{rename.NewName}");
                    break;
                case CreateIndexOperation create:
                    explained.Add($"index:{create.Table}.{create.Name}");
                    break;
                case DropIndexOperation drop:
                    explained.Add($"index:{drop.Table}.{drop.Name}");
                    break;
                case RenameIndexOperation rename:
                    explained.Add($"index:{rename.Table}.{rename.Name}");
                    explained.Add($"index:{rename.Table}.{rename.NewName}");
                    break;
                case AddForeignKeyOperation add:
                    explained.Add($"foreign key:{add.Table}.{add.Name}");
                    break;
                case DropForeignKeyOperation drop:
                    explained.Add($"foreign key:{drop.Table}.{drop.Name}");
                    break;
                case AddPrimaryKeyOperation add:
                    explained.Add($"primary key:{add.Table}.{add.Name}");
                    break;
                case DropPrimaryKeyOperation drop:
                    explained.Add($"primary key:{drop.Table}.{drop.Name}");
                    break;
                case AddUniqueConstraintOperation add:
                    explained.Add($"unique constraint:{add.Table}.{add.Name}");
                    break;
                case DropUniqueConstraintOperation drop:
                    explained.Add($"unique constraint:{drop.Table}.{drop.Name}");
                    break;
                case AlterTableOperation alter:
                    explained.Add($"table:{alter.Name}");
                    break;
            }
        }

        bool IsExplained(string key, ShapeEntry entry) => explained.Contains(key) || wholeTables.Contains(entry.Table);

        foreach (var key in before.Keys.Except(after.Keys)
                     .Where(key => !IsExplained(key, before[key]))
                     .Order(StringComparer.Ordinal))
            yield return $"{key} was removed";

        foreach (var key in after.Keys.Except(before.Keys)
                     .Where(key => !IsExplained(key, after[key]))
                     .Order(StringComparer.Ordinal))
            yield return $"{key} was added";

        foreach (var key in before.Keys.Intersect(after.Keys)
                     .Where(key => before[key].Value != after[key].Value && !IsExplained(key, after[key]))
                     .Order(StringComparer.Ordinal))
            yield return $"{key} changed from '{before[key].Value}' to '{after[key].Value}'";
    }
}
