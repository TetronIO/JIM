// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
        var message = ImportOutcomeMessage.ForImport(objects: 1_000_000, created: 999_880, updated: 0, unchanged: 0, errors: 120, throughput: " in 18 min 13 sec (avg 915 obj/s)");

        Assert.That(message, Is.EqualTo("Import complete: 1,000,000 objects (999,880 created, 0 updated, 120 errors) in 18 min 13 sec (avg 915 obj/s)"));
    }

    [Test]
    public void ForImport_NothingUnchangedAndNoErrors_LeavesThoseOut()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 2, created: 2, updated: 0, unchanged: 0, errors: 0, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 2 objects (2 created, 0 updated)"));
    }

    [Test]
    public void ForImport_UnchangedObjects_AreCounted()
    {
        var message = ImportOutcomeMessage.ForImport(objects: 50, created: 0, updated: 3, unchanged: 47, errors: 1, throughput: string.Empty);

        Assert.That(message, Is.EqualTo("Import complete: 50 objects (0 created, 3 updated, 47 unchanged, 1 error)"));
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
}
