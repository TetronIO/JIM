// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.ObjectModel;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// No navigation onto an object's values may be a <see cref="List{T}"/> or a <see cref="Collection{T}"/> (#2598, Phase 4).
/// EF Core checks whether such a collection already holds an entity, before adding each one it loads or fixes up into
/// it, by scanning it, so a group's values loaded in time that grew with the square of its membership (41 s for one
/// group of 200,000 members). Any other collection type is asked itself, which is what
/// <c>ReferenceIndexedList</c> answers in constant time. A value navigation added or retyped as a list would bring the
/// quadratic load back without failing anything else.
/// </summary>
[TestFixture]
public class ValueCollectionNavigationTypeTests
{
    // The entities a single object can hold hundreds of thousands of: a group's member values and the changes to them.
    private static readonly Type[] ValueEntityTypes =
    [
        typeof(ConnectedSystemObjectAttributeValue),
        typeof(MetaverseObjectAttributeValue),
        typeof(PendingExportAttributeValueChange)
    ];

    [Test]
    public void ValueCollectionNavigations_NoneIsAListOrCollection()
    {
        using var context = new JimDbContext(new DbContextOptionsBuilder<JimDbContext>().UseNpgsql("Host=localhost").Options);
        var navigations = context.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetNavigations())
            .Where(navigation => navigation.IsCollection && ValueEntityTypes.Contains(navigation.TargetEntityType.ClrType))
            .ToList();

        var scanned = navigations
            .Where(navigation => IsScannedByEfCore(navigation.ClrType))
            .Select(navigation => $"{navigation.DeclaringEntityType.ClrType.Name}.{navigation.Name} ({navigation.ClrType.Name})")
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(navigations.Select(n => n.TargetEntityType.ClrType).Distinct(), Is.EquivalentTo(ValueEntityTypes),
                "every value entity is reached by a collection navigation; if one no longer is, update this list");
            Assert.That(scanned, Is.Empty,
                "these value navigations are lists, which EF Core scans before every add; make them a ReferenceIndexedList");
        }
    }

    private static bool IsScannedByEfCore(Type collectionType)
    {
        for (var type = collectionType; type != null; type = type.BaseType)
        {
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(Collection<>)))
                return true;
        }
        return false;
    }
}
