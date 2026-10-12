// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins the sentence a finished import leaves on its Activity (#170): the counts, their grouping and the
/// throughput suffix, so it reads like the synchronisation and export processors' messages.
/// </summary>
[TestFixture]
public class ImportOutcomeMessageTests
{
    [Test]
    public void ForImport_LargeCountsWithErrors_GroupsTheDigitsAndNamesTheErrors()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 1_000_000, created: 999_880, updated: 0, unchanged: 0,
            connectorReportedDeletions: 0, detectedDeletions: 0, errors: 120, throughput: " in 18 min 13 sec (avg 915 obj/s)");

        Assert.That(message, Is.EqualTo("Import complete: 1,000,000 objects (999,880 created, 0 updated, 120 errors) in 18 min 13 sec (avg 915 obj/s)"));
    }

    [Test]
    public void ForImport_NothingUnchangedDeletedOrInError_LeavesThoseOut()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 2, created: 2, updated: 0, unchanged: 0,
            connectorReportedDeletions: 0, detectedDeletions: 0, errors: 0, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 2 objects (2 created, 0 updated)"));
    }

    [Test]
    public void ForImport_UnchangedObjects_AreCounted()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 50, created: 0, updated: 3, unchanged: 47,
            connectorReportedDeletions: 0, detectedDeletions: 0, errors: 1, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 50 objects (0 created, 3 updated, 47 unchanged, 1 error)"));
    }

    [Test]
    public void ForImport_DeletionsTheConnectorReported_AreCountedAmongTheObjectsRead()
    {
        // The Connector returned these objects, as deletes, so they are among the objects the run read (#2129).
        var message = ImportOutcomeMessage.ForImport(objects: 10, created: 0, updated: 1, unchanged: 6,
            connectorReportedDeletions: 2, detectedDeletions: 0, errors: 1, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 10 objects (0 created, 1 updated, 6 unchanged, 2 deleted, 1 error)"));
    }

    [Test]
    public void ForImport_DeletionsAFullImportDetected_AreReportedApartFromTheObjectsRead()
    {
        // A Full Import marks these deleted because it did not read them, so counting them among the objects it read would
        // make the figures add up to more than it read (#2129).
        var message = ImportOutcomeMessage.ForImport(objects: 10, created: 0, updated: 0, unchanged: 10,
            connectorReportedDeletions: 0, detectedDeletions: 500, errors: 0, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 10 objects (0 created, 0 updated, 10 unchanged); 500 no longer found and marked as deleted"));
    }

    [Test]
    public void ForImport_DetectedDeletionsAndThroughput_KeepsTheThroughputWithTheObjectsRead()
    {
        // The throughput is the objects read over the run's time, so it follows them rather than the detected deletions,
        // where it would read as the time the deletions took.
        var message = ImportOutcomeMessage.ForImport(objects: 60_000, created: 0, updated: 12, unchanged: 59_988,
            connectorReportedDeletions: 0, detectedDeletions: 1_250, errors: 0, throughput: " in 3 min 12 sec (avg 312 obj/s)");

        Assert.That(message, Is.EqualTo(
            "Import complete: 60,000 objects (0 created, 12 updated, 59,988 unchanged) in 3 min 12 sec (avg 312 obj/s); 1,250 no longer found and marked as deleted"));
    }

    [Test]
    public void ForImport_OneOfEach_UsesSingularWording()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 1, created: 0, updated: 0, unchanged: 1,
            connectorReportedDeletions: 0, detectedDeletions: 1, errors: 1, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 1 object (0 created, 0 updated, 1 unchanged, 1 error); 1 no longer found and marked as deleted"));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // ForRefusedDeletionDetection: Run Profile Safeguards (#1618, Layer 2)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void ForRefusedDeletionDetection_PercentLimitTripped_NamesThePercentLimit()
    {
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 4120, baseCount: 10000, maxCount: null, maxPercent: 10);

        Assert.That(message, Is.EqualTo(
            "Deletion detection found 4,120 objects (41% of 10,000) no longer in the Connected System, above this Run Profile's " +
            "limit of 10%; none were marked as deleted. Check the Connected System's scope and the connector's filters, or raise " +
            "the limit, then run the Full Import again."));
    }

    [Test]
    public void ForRefusedDeletionDetection_CountLimitTripped_NamesTheCountLimit()
    {
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 501, baseCount: 10000, maxCount: 500, maxPercent: null);

        Assert.That(message, Is.EqualTo(
            "Deletion detection found 501 objects (5% of 10,000) no longer in the Connected System, above this Run Profile's " +
            "limit of 500; none were marked as deleted. Check the Connected System's scope and the connector's filters, or raise " +
            "the limit, then run the Full Import again."));
    }

    [Test]
    public void ForRefusedDeletionDetection_BothLimitsTripped_NamesBothLimits()
    {
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 4120, baseCount: 10000, maxCount: 500, maxPercent: 10);

        Assert.That(message, Does.Contain("above this Run Profile's limits of 500 and 10%;"));
    }

    [Test]
    public void ForRefusedDeletionDetection_OnlyCountLimitConfiguredButPercentAlsoTrips_NamesOnlyWhatConfigured()
    {
        // A limit that was never configured (null) cannot have tripped, whatever the numbers say, so it
        // is never named even when the arithmetic would otherwise call it tripped.
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 4120, baseCount: 10000, maxCount: 500, maxPercent: null);

        Assert.That(message, Does.Contain("above this Run Profile's limit of 500;"));
    }

    [Test]
    public void ForRefusedDeletionDetection_SingleObject_UsesSingularWording()
    {
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 1, baseCount: 10, maxCount: 0, maxPercent: null);

        Assert.That(message, Does.StartWith("Deletion detection found 1 object (10% of 10) no longer in the Connected System"));
    }

    [Test]
    public void ForRefusedDeletionDetection_PercentRoundsToNearestWholeNumber()
    {
        var message = ImportOutcomeMessage.ForRefusedDeletionDetection(count: 1, baseCount: 3, maxCount: 0, maxPercent: null);

        // 1 of 3 is 33.33...%, which rounds to 33%.
        Assert.That(message, Does.Contain("(33% of 3)"));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // ForDeselectedObjectTypesHeldBack: deselected Object Types still managed by enabled Synchronisation Rules (#1474)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void ForDeselectedObjectTypesHeldBack_OneTypeOneRule_NamesTheTypeTheRuleAndTheFix()
    {
        var message = ImportOutcomeMessage.ForDeselectedObjectTypesHeldBack([("group", ["Provision groups"])]);

        Assert.That(message, Is.EqualTo(
            "Object Type 'group' is deselected, but its objects were not checked for deletion because an enabled " +
            "Synchronisation Rule still manages it: Provision groups. They stay joined and are no longer refreshed. " +
            "Disable that Synchronisation Rule to take the Object Type out of management, or select the Object Type again."));
    }

    [Test]
    public void ForDeselectedObjectTypesHeldBack_SeveralRules_UsesPluralWording()
    {
        var message = ImportOutcomeMessage.ForDeselectedObjectTypesHeldBack([("group", ["Import groups", "Provision groups"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("because 2 enabled Synchronisation Rules still manage it: Import groups, Provision groups."));
            Assert.That(message, Does.Contain("Disable those Synchronisation Rules"));
        }
    }

    [Test]
    public void ForDeselectedObjectTypesHeldBack_SeveralTypes_OneSentencePerType()
    {
        var message = ImportOutcomeMessage.ForDeselectedObjectTypesHeldBack([("contact", ["Contacts"]), ("group", ["Groups"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.StartWith("Object Type 'contact' is deselected"));
            Assert.That(message, Does.Contain(" Object Type 'group' is deselected"));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // ForUnresolvedReferences: one item error covering every reference the object left unresolved (#959)
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public void ForUnresolvedReferences_OneValueMatchingNothing_NamesTheAttributeTheValueAndBothScopeCauses()
    {
        var message = ImportOutcomeMessage.ForUnresolvedReferences(
            [("manager", "CN=Mark Doyle,OU=Contractors,DC=corp", null)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("manager"));
            Assert.That(message, Does.Contain("CN=Mark Doyle,OU=Contractors,DC=corp"));
            Assert.That(message, Does.Contain("Container Scope"));
            Assert.That(message, Does.Contain("exclude"),
                "An excluding Container is the harder cause to spot (#1255), so the message must name it.");
            Assert.That(message, Does.Not.Contain("there may be more"));
        }
    }

    [Test]
    public void ForUnresolvedReferences_SeveralValues_CountsThemPerAttributeRatherThanNamingOnlyTheFirst()
    {
        var message = ImportOutcomeMessage.ForUnresolvedReferences(
        [
            ("member", "CN=Ana Silva,OU=Contractors,DC=corp", null),
            ("manager", "CN=Mark Doyle,OU=Contractors,DC=corp", null),
            ("member", "CN=Ben Ortiz,OU=Contractors,DC=corp", null)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("3 references"));
            Assert.That(message, Does.Contain("1 on manager, 2 on member"),
                "The breakdown is per attribute, in a stable order, so two runs with the same problem read the same.");
            Assert.That(message, Does.Contain("Container Scope"));
            Assert.That(message, Does.Not.Contain("there may be more"));
        }
    }

    [Test]
    public void ForUnresolvedReferences_LargeCount_GroupsTheDigits()
    {
        var references = Enumerable.Range(0, 1_284)
            .Select(i => ("member", $"CN=User {i},OU=Contractors,DC=corp", (IReadOnlyList<string>?)null))
            .ToList();

        var message = ImportOutcomeMessage.ForUnresolvedReferences(references);

        Assert.That(message, Does.Contain("1,284 references").And.Contain("1,284 on member"));
    }

    [Test]
    public void ForUnresolvedReferences_OneAmbiguousValue_NamesTheCandidateObjectTypesAndTheSchemaFix()
    {
        var message = ImportOutcomeMessage.ForUnresolvedReferences(
            [("member", "42", ["SOURCE_USER", "SOURCE_GROUP"])]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("SOURCE_GROUP").And.Contain("SOURCE_USER"));
            Assert.That(message, Does.Contain("member"));
            Assert.That(message, Does.Contain("schema configuration"));
            Assert.That(message, Does.Not.Contain("Container Scope"),
                "An ambiguous value was found, twice; scope is not its cause, and saying so would mislead.");
        }
    }

    [Test]
    public void ForUnresolvedReferences_AmbiguousAndMissingTogether_GivesEachCauseItsOwnRemedy()
    {
        var message = ImportOutcomeMessage.ForUnresolvedReferences(
        [
            ("member", "CN=Ana Silva,OU=Contractors,DC=corp", null),
            ("member", "42", ["SOURCE_USER", "SOURCE_GROUP"]),
            ("member", "CN=Ben Ortiz,OU=Contractors,DC=corp", null)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("3 references"));
            Assert.That(message, Does.Contain("2 match no Connected System Object"));
            Assert.That(message, Does.Contain("Container Scope"));
            Assert.That(message, Does.Contain("1 matches objects of more than one Object Type"));
            Assert.That(message, Does.Contain("schema configuration"));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // ForUnreadableAttributeValues / ForUnreadableExternalIds (#2066)
    // -----------------------------------------------------------------------------------------------------------------

    private static ConnectedSystemImportObjectAttributeValueError Unreadable(string attributeName, string? value, string reason = "Not a valid whole number.") =>
        new() { AttributeName = attributeName, Value = value, Reason = reason };

    [Test]
    public void ForUnreadableAttributeValues_OneValue_NamesTheAttributeTheValueAndWhy()
    {
        var message = ImportOutcomeMessage.ForUnreadableAttributeValues([Unreadable("startDate", "31/02/2026", "Not a valid date and time")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("startDate").And.Contain("31/02/2026").And.Contain("Not a valid date and time."));
            Assert.That(message, Does.Contain("left startDate as it already held it"), "The message says what JIM did about it.");
        }
    }

    [Test]
    public void ForUnreadableAttributeValues_SeveralValues_CountsThemPerAttributeAndListsEach()
    {
        var message = ImportOutcomeMessage.ForUnreadableAttributeValues(
        [
            Unreadable("Rates", "lots"),
            Unreadable("Rates", "heaps"),
            Unreadable("startDate", "31/02/2026")
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("3 values (2 on Rates, 1 on startDate)"));
            Assert.That(message, Does.Contain("lots").And.Contain("heaps").And.Contain("31/02/2026"));
        }
    }

    [Test]
    public void ForUnreadableAttributeValues_ManyValues_ListsTheFirstFiveAndCountsTheRest()
    {
        var values = Enumerable.Range(1, 8).Select(i => Unreadable("Grades", $"bad{i}")).ToList();

        var message = ImportOutcomeMessage.ForUnreadableAttributeValues(values);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("bad5"));
            Assert.That(message, Does.Not.Contain("bad6"));
            Assert.That(message, Does.Contain("3 more"));
        }
    }

    [Test]
    public void ForUnreadableAttributeValues_LongValue_IsShortened()
    {
        var message = ImportOutcomeMessage.ForUnreadableAttributeValues([Unreadable("notes", new string('x', 5_000))]);

        Assert.That(message.Length, Is.LessThan(500), "The item says what is wrong; it is not a place to keep a payload.");
    }

    [Test]
    public void ForUnreadableAttributeValues_ValueTheConnectorCouldNotRender_IsLeftOut()
    {
        var message = ImportOutcomeMessage.ForUnreadableAttributeValues([Unreadable("photo", null, "Not valid base64-encoded binary.")]);

        Assert.That(message, Does.Contain("photo").And.Not.Contain("''"));
    }

    [Test]
    public void ForUnreadableExternalIds_NamesEachObjectTypeAndHowManyObjects()
    {
        var message = ImportOutcomeMessage.ForUnreadableExternalIds([("User", 1), ("Group", 3)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("'User'").And.Contain("1 object "));
            Assert.That(message, Does.Contain("'Group'").And.Contain("3 objects"));
            Assert.That(message, Does.Contain("None of its objects were marked as deleted"));
        }
    }
}
