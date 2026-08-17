// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Staging;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Turns what a sample of a Connected System's objects carried into suggestions an administrator can act on.
/// </summary>
/// <remarks>
/// The Connector counts every class it saw, because which classes are auxiliary is JIM's knowledge rather than the
/// Connector's. This applies that knowledge: the structural class the sample was taken from, the abstract classes
/// above it, and anything the schema does not publish are all noise here; only auxiliary classes are something an
/// administrator could choose to manage.
/// </remarks>
[TestFixture]
public class AuxiliaryClassUsageAggregatorTests
{
    private const int PersonId = 1;

    private static ConnectedSystemObjectType ObjectType(int id, string name, string? classKind)
    {
        var objectType = new ConnectedSystemObjectType { Id = id, Name = name };
        if (classKind != null)
            objectType.Tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = classKind });

        return objectType;
    }

    private static List<ConnectedSystemObjectType> Schema()
    {
        return
        [
            ObjectType(PersonId, "inetOrgPerson", ObjectTypeTags.Values.ClassKindStructural),
            ObjectType(2, "posixAccount", ObjectTypeTags.Values.ClassKindAuxiliary),
            ObjectType(3, "shadowAccount", ObjectTypeTags.Values.ClassKindAuxiliary),
            ObjectType(4, "top", ObjectTypeTags.Values.ClassKindAbstract),
            ObjectType(5, "unclassified", classKind: null)
        ];
    }

    private static ObjectClassUsageResult Usage(int entriesRead, params (string ClassName, int Count)[] counts)
    {
        var usage = new ObjectClassUsageResult { EntriesRead = entriesRead };
        foreach (var (className, count) in counts)
            usage.ObjectClassCounts[className] = count;

        return usage;
    }

    private static ConnectedSystemObjectType Person()
    {
        return Schema().Single(objectType => objectType.Id == PersonId);
    }

    [Test]
    public void Aggregate_ForAnAuxiliaryClassTheObjectsCarried_ReportsItWithItsCount()
    {
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("inetOrgPerson", 100), ("posixAccount", 80)), Schema());

        Assert.That(aggregation.Results, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(aggregation.Results[0].AuxiliaryClassName, Is.EqualTo("posixAccount"));
            Assert.That(aggregation.Results[0].EntryCount, Is.EqualTo(80));
            Assert.That(aggregation.Results[0].StructuralObjectTypeId, Is.EqualTo(PersonId));
        }
    }

    [Test]
    public void Aggregate_DoesNotReportTheStructuralClassTheSampleWasTakenFrom()
    {
        // Every object of this type carries it by definition, so offering it as something to attach would be
        // nonsense.
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("inetOrgPerson", 100)), Schema());

        Assert.That(aggregation.Results, Is.Empty);
    }

    [Test]
    public void Aggregate_DoesNotReportAbstractOrUnclassifiedClasses()
    {
        // top is on every object and can never be instantiated. An unclassified type is one JIM knows nothing
        // about, and guessing that it is auxiliary would put a structural class in front of an administrator as
        // something to attach.
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("top", 100), ("unclassified", 40)), Schema());

        Assert.That(aggregation.Results, Is.Empty);
    }

    [Test]
    public void Aggregate_ForAClassTheSchemaDoesNotPublish_ReportsItUnrecognisedRatherThanSuggestingIt()
    {
        // An object carrying a class the subschema does not publish is a directory contradicting itself. JIM cannot
        // offer it, because there is no Object Type to attach, but staying silent would leave an administrator
        // wondering why a class they can see on their own objects never appears.
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("posixAccount", 80), ("mysteryClass", 5)), Schema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aggregation.Results.Select(result => result.AuxiliaryClassName), Is.EquivalentTo(new[] { "posixAccount" }));
            Assert.That(aggregation.UnrecognisedClasses, Is.EquivalentTo(new[] { "mysteryClass" }));
        }
    }

    [Test]
    public void Aggregate_MatchesClassNamesWithoutRegardToCase()
    {
        // A directory does not have to spell a class on an object the same way its schema spells it.
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("POSIXACCOUNT", 80)), Schema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aggregation.Results, Has.Count.EqualTo(1));
            Assert.That(aggregation.Results[0].AuxiliaryClassName, Is.EqualTo("posixAccount"),
                "the schema's spelling is the one that matches an Object Type");
        }
    }

    [Test]
    public void Aggregate_OrdersTheMostUsedClassFirst()
    {
        // An administrator scanning suggestions should meet the class most of their objects carry first.
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(
            Person(), Usage(100, ("shadowAccount", 10), ("posixAccount", 80)), Schema());

        Assert.That(aggregation.Results.Select(result => result.AuxiliaryClassName),
            Is.EqualTo(new[] { "posixAccount", "shadowAccount" }));
    }

    [Test]
    public void Aggregate_WhenNothingWasRead_ReportsNothing()
    {
        var aggregation = AuxiliaryClassUsageAggregator.Aggregate(Person(), Usage(0), Schema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(aggregation.Results, Is.Empty);
            Assert.That(aggregation.UnrecognisedClasses, Is.Empty);
        }
    }
}
