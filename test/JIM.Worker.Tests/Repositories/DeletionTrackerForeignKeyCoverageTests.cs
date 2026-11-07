// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Holds both raw deletions' tracker fix-ups to every foreign key in the model that points at what they delete (#2799).
/// </summary>
/// <remarks>
/// <para>
/// Connected System Objects and Metaverse Objects are deleted with raw SQL, so the code that issues the delete brings
/// the change tracker into line with what the database did (<c>src/CLAUDE.md</c>, "Raw SQL Writes Must Fix Up or
/// Detach Tracked Instances"): a tracked row the database deleted by cascade is detached, and a tracked row whose
/// reference the database cleared has the null recorded as already persisted. A foreign key the fix-up does not know
/// about leaves the stale id in the tracker; the next save of that row on the same context writes the id back, fails
/// with <c>23503</c>, and poisons every later save on the Worker's context. That is how
/// <c>PendingExchangeChanges.ConnectedSystemObjectId</c> was missed when the Exchange queue was added.
/// </para>
/// <para>
/// Each foreign key into the two tables is exercised on its own: a row of the referencing type is tracked, unchanged,
/// naming a deleted object, and the fix-up must detach it (cascade) or clear the reference (anything else, since a
/// delete that succeeded left nothing in the column). The model is enumerated, not a list, so a foreign key added later
/// fails here until the fix-up handles it. No database is needed: the fix-ups read only the tracker.
/// </para>
/// </remarks>
[TestFixture]
public class DeletionTrackerForeignKeyCoverageTests
{
    private static readonly Type[] DeletedTypes = [typeof(ConnectedSystemObject), typeof(MetaverseObject)];

    private static JimDbContext NewModelContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql("Host=model-only;Database=model-only;Username=model-only;Password=model-only")
        .Options);

    private static IEnumerable<TestCaseData> ForeignKeys()
    {
        using var context = NewModelContext();
        return context.Model.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredForeignKeys())
            .Where(fk => DeletedTypes.Contains(fk.PrincipalEntityType.ClrType))
            .Select(fk => new TestCaseData(fk.DeclaringEntityType.Name, string.Join(",", fk.Properties.Select(p => p.Name)), fk.PrincipalEntityType.ClrType)
                .SetName($"{{m}}({fk.DeclaringEntityType.ShortName()}.{string.Join(",", fk.Properties.Select(p => p.Name))} -> {fk.PrincipalEntityType.ShortName()})"))
            .ToList();
    }

    [Test]
    public void ForeignKeys_IntoBothDeletedTables_AreFound()
    {
        var found = ForeignKeys().Select(c => (Type)c.Arguments[2]!).Distinct().ToList();
        Assert.That(found, Is.EquivalentTo(DeletedTypes), "the enumeration finds foreign keys into both tables, so the cases below are not vacuous");
    }

    [TestCaseSource(nameof(ForeignKeys))]
    public void DeletionTrackerFixUp_RowReferencingADeletedObject_IsDetachedOrClearedAsTheDatabaseDid(
        string dependentEntityTypeName, string foreignKeyProperties, Type deletedType)
    {
        using var context = NewModelContext();
        var dependentType = context.Model.FindEntityType(dependentEntityTypeName)!;
        var foreignKey = dependentType.GetDeclaredForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == deletedType
                          && string.Join(",", fk.Properties.Select(p => p.Name)) == foreignKeyProperties);
        Assert.That(foreignKey.Properties, Has.Count.EqualTo(1),
            "both fix-ups read a reference as one column; a composite foreign key into these tables needs them extended");

        var deletedId = Guid.NewGuid();
        var entry = TrackUnchangedRowReferencing(context, dependentType, foreignKey, deletedId);
        var keyProperty = entry.Property(foreignKey.Properties[0].Name);
        Assert.That(entry.State, Is.EqualTo(EntityState.Unchanged), "precondition: the row is tracked unchanged, as a page loads it");

        if (deletedType == typeof(ConnectedSystemObject))
            ConnectedSystemObjectDeletionSql.UpdateTracker(context, [deletedId]);
        else
            SyncRepository.ReflectDeletedMetaverseObjectsInTracker(context, [deletedId]);

        var cascades = foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade;
        var remedy = deletedType == typeof(ConnectedSystemObject)
            ? "handle it in ConnectedSystemObjectDeletionSql (IsDeletedWithTheObjects for a cascade, ClearReferencesToTheObjects otherwise)"
            : "handle it in SyncRepository.ReflectDeletedMetaverseObjectsInTracker";
        if (cascades)
        {
            Assert.That(entry.State, Is.EqualTo(EntityState.Detached),
                $"the database deletes this row with the object ({foreignKey.DeleteBehavior}), so the tracker must let go of it; {remedy}");
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keyProperty.CurrentValue, Is.Null,
                $"the database holds null here once the object is deleted ({foreignKey.DeleteBehavior}), so the tracker must too, or a later save writes the deleted id back and fails with 23503; {remedy}");
            Assert.That(keyProperty.OriginalValue, Is.Null, "the null is recorded as already persisted");
            Assert.That(keyProperty.IsModified, Is.False, "so no save writes it");
            Assert.That(entry.State, Is.EqualTo(EntityState.Unchanged), "and the row stays unchanged");
        }
    }

    /// <summary>
    /// Tracks a new row of <paramref name="dependentType"/>, unchanged, with every key column set and the foreign key
    /// naming <paramref name="deletedId"/>. Shared-type rows (a many-to-many join) are property bags; everything else
    /// is its CLR type, built without its constructor's or initialisers' opinions about required members.
    /// </summary>
    private static EntityEntry TrackUnchangedRowReferencing(JimDbContext context, IEntityType dependentType, IForeignKey foreignKey, Guid deletedId)
    {
        var foreignKeyProperty = foreignKey.Properties[0];
        var seed = 1;
        object SampleFor(IProperty property) => property == foreignKeyProperty
            ? deletedId
            : (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) switch
            {
                var t when t == typeof(Guid) => Guid.NewGuid(),
                var t when t == typeof(int) => seed++,
                var t when t == typeof(long) => (long)seed++,
                var t when t == typeof(string) => $"key-{seed++}",
                var t => throw new NotSupportedException($"No sample key value for {t.Name} ({dependentType.ShortName()}.{property.Name})")
            };

        var assigned = dependentType.FindPrimaryKey()!.Properties.Append(foreignKeyProperty).Distinct().ToList();

        if (dependentType.IsPropertyBag)
        {
            var bag = assigned.ToDictionary(p => p.Name, SampleFor);
            var bagEntry = context.Set<Dictionary<string, object>>(dependentType.Name).Attach(bag);
            return bagEntry;
        }

        var row = Activator.CreateInstance(dependentType.ClrType, nonPublic: true)!;
        var shadow = new List<(IProperty Property, object Value)>();
        foreach (var property in assigned)
        {
            var value = SampleFor(property);
            if (property.PropertyInfo != null)
                property.PropertyInfo.SetValue(row, value);
            else
                shadow.Add((property, value));
        }

        var entry = context.Entry(row);
        entry.State = EntityState.Unchanged;
        foreach (var (property, value) in shadow)
        {
            var shadowEntry = entry.Property(property.Name);
            shadowEntry.CurrentValue = value;
            shadowEntry.OriginalValue = value;
            shadowEntry.IsModified = false;
        }

        return entry;
    }
}
