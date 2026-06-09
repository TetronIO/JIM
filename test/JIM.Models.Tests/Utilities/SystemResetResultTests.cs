// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

public class SystemResetResultTests
{
    [Test]
    public void BuildResetMessage_AdministratorsRemoved_IncludesHeaderAndBulletedNonZeroCounts()
    {
        var result = new SystemResetResult
        {
            ConnectedSystemsRemoved = 2,
            MetaverseObjectsRemoved = 1119,
            SyncRulesRemoved = 1,
            AdministratorsRemoved = 3
        };

        var message = result.BuildResetMessage(includeAdministrators: true);

        Assert.That(message, Does.StartWith("Factory reset completed (administrators removed)."));
        Assert.That(message, Does.Contain("Removed:"));
        Assert.That(message, Does.Contain("• 2 connected systems"));
        Assert.That(message, Does.Contain("• 1,119 metaverse objects"));
        // Count of 1 uses the singular form.
        Assert.That(message, Does.Contain("• 1 synchronisation rule"));
        Assert.That(message, Does.Not.Contain("• 1 synchronisation rules"));
        // Categories with a zero count are omitted entirely.
        Assert.That(message, Does.Not.Contain("schedule"));
        Assert.That(message, Does.Not.Contain("certificate"));
    }

    [Test]
    public void BuildResetMessage_AdministratorsRetained_StatesRetainedCount()
    {
        var result = new SystemResetResult
        {
            MetaverseObjectsRemoved = 5,
            AdministratorsRetained = 2
        };

        var message = result.BuildResetMessage(includeAdministrators: false);

        Assert.That(message, Does.StartWith("Factory reset completed (administrators retained: 2)."));
        Assert.That(message, Does.Contain("• 5 metaverse objects"));
    }

    [Test]
    public void BuildResetMessage_NewlyCountedTypes_AppearAsBullets()
    {
        var result = new SystemResetResult
        {
            ObjectMatchingRulesRemoved = 4,
            CustomExampleDataTemplatesRemoved = 2,
            MetaverseObjectChangesRemoved = 50,
            ConnectedSystemObjectChangesRemoved = 60,
            ScheduleExecutionsRemoved = 9
        };

        var message = result.BuildResetMessage(includeAdministrators: true);

        Assert.That(message, Does.Contain("• 4 object matching rules"));
        Assert.That(message, Does.Contain("• 2 custom example data templates"));
        Assert.That(message, Does.Contain("• 50 metaverse object change records"));
        Assert.That(message, Does.Contain("• 60 connected system object change records"));
        Assert.That(message, Does.Contain("• 9 schedule executions"));
    }

    [Test]
    public void BuildResetMessage_IrregularPlurals_ArePluralisedCorrectly()
    {
        var result = new SystemResetResult
        {
            ActivitiesRemoved = 3,
            CustomPredefinedSearchesRemoved = 2
        };

        var message = result.BuildResetMessage(includeAdministrators: true);

        Assert.That(message, Does.Contain("• 3 activities"));
        Assert.That(message, Does.Contain("• 2 custom predefined searches"));
    }

    [Test]
    public void BuildResetMessage_SingleActivity_UsesSingularForm()
    {
        var result = new SystemResetResult { ActivitiesRemoved = 1 };

        var message = result.BuildResetMessage(includeAdministrators: true);

        Assert.That(message, Does.Contain("• 1 activity"));
        Assert.That(message, Does.Not.Contain("activities"));
    }

    [Test]
    public void BuildResetMessage_NothingRemoved_StatesEmptySystem()
    {
        var result = new SystemResetResult { AdministratorsRetained = 1 };

        var message = result.BuildResetMessage(includeAdministrators: false);

        Assert.That(message, Does.StartWith("Factory reset completed (administrators retained: 1)."));
        Assert.That(message, Does.Contain("Nothing was removed"));
        Assert.That(message, Does.Not.Contain("Removed:"));
    }
}
