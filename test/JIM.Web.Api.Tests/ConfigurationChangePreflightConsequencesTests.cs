// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The headline is the sentence an administrator reads before consenting to a destructive configuration change, so it
/// has to describe the thing they actually did. Two shapes reach this dialog: a property whose value decides whether
/// objects are removed, and a whole item taken out of a collection (a container leaving a Connected System's import
/// scope). Calling the second one "this property" describes neither the container nor the act.
/// </summary>
[TestFixture]
public class ConfigurationChangePreflightConsequencesTests
{
    [Test]
    public void For_NoDestructiveItems_ReturnsNull()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Name", ConfigurationChangeClass.Cosmetic)));

        Assert.That(group, Is.Null, "a change with nothing destructive in it has no consequences to lead with");
    }

    [Test]
    public void For_DestructiveItemWithoutConsequenceCopy_IsNotListed()
    {
        // The group leads the dialog; an item with nothing to say would be a bullet point naming a property and
        // stating nothing about it.
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Deprovisioning Action", ConfigurationChangeClass.Destructive)));

        Assert.That(group, Is.Null);
    }

    [Test]
    public void For_OneDestructiveProperty_HeadlineNamesTheProperty()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Deprovisioning Action", ConfigurationChangeClass.Destructive, consequence: "Objects will be deleted.")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group!.Headline, Is.EqualTo("This property decides whether objects are removed"));
            Assert.That(group.Items.Single().Text, Does.StartWith("Deprovisioning Action: "));
        }
    }

    [Test]
    public void For_SeveralDestructiveProperties_HeadlineIsPlural()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Deprovisioning Action", ConfigurationChangeClass.Destructive, consequence: "Objects will be deleted."),
            Item("Deletion Rule", ConfigurationChangeClass.Destructive, consequence: "Objects become eligible for deletion.")));

        Assert.That(group!.Headline, Is.EqualTo("These properties decide whether objects are removed"));
    }

    [Test]
    public void For_OneRemovedCollectionItem_HeadlineDescribesTheRemovalNotAProperty()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Containers > Service Accounts", ConfigurationChangeClass.Destructive,
                consequence: "Deselecting this container stops the objects beneath it being imported.",
                changeType: ConfigurationDiffChangeType.Removed, isCollectionItem: true)));

        Assert.That(group!.Headline, Is.EqualTo("Removing this takes objects out of scope"));
    }

    [Test]
    public void For_SeveralRemovedCollectionItems_HeadlineIsPlural()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Containers > Service Accounts", ConfigurationChangeClass.Destructive, consequence: "Deselecting this container...",
                changeType: ConfigurationDiffChangeType.Removed, isCollectionItem: true),
            Item("Containers > Contractors", ConfigurationChangeClass.Destructive, consequence: "Deselecting this container...",
                changeType: ConfigurationDiffChangeType.Removed, isCollectionItem: true)));

        Assert.That(group!.Headline, Is.EqualTo("Removing these takes objects out of scope"));
    }

    [Test]
    public void For_PropertyAndRemovedCollectionItemTogether_HeadlineCoversBoth()
    {
        // One save can do both. Neither of the specific headlines is true of the whole, so the wording widens rather
        // than picking one and being wrong about the other half.
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Deprovisioning Action", ConfigurationChangeClass.Destructive, consequence: "Objects will be deleted."),
            Item("Containers > Service Accounts", ConfigurationChangeClass.Destructive, consequence: "Deselecting this container...",
                changeType: ConfigurationDiffChangeType.Removed, isCollectionItem: true)));

        Assert.That(group!.Headline, Is.EqualTo("These changes decide whether objects are removed"));
    }

    [Test]
    public void For_QualifiedLabel_StatesOnlyItsLeaf()
    {
        // The dialog lists every changing property directly beneath this alert, each under its full qualified label,
        // so spelling the whole path out here printed the same string twice on one screen (#1275).
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Partitions > dc=corp,dc=local > Containers > ou=Contractors,dc=corp,dc=local",
                ConfigurationChangeClass.Destructive, consequence: "Deselecting this container...",
                changeType: ConfigurationDiffChangeType.Removed, isCollectionItem: true)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group!.Items.Single().Text,
                Is.EqualTo("ou=Contractors,dc=corp,dc=local: Deselecting this container..."));
            Assert.That(group.Items.Single().Text, Does.Not.Contain("Partitions >"));
        }
    }

    [Test]
    public void For_UnqualifiedLabel_IsStatedWhole()
    {
        var group = ConfigurationChangePreflightConsequences.For(Preflight(
            Item("Deprovisioning Action", ConfigurationChangeClass.Destructive, consequence: "Objects will be deleted.")));

        Assert.That(group!.Items.Single().Text, Is.EqualTo("Deprovisioning Action: Objects will be deleted."));
    }

    private static ConfigurationChangePreflight Preflight(params ConfigurationChangePreflightItem[] items) =>
        new() { HighestClass = items.Max(i => i.Class), Items = items };

    private static ConfigurationChangePreflightItem Item(string label, ConfigurationChangeClass changeClass,
        string? consequence = null, ConfigurationDiffChangeType changeType = ConfigurationDiffChangeType.Modified,
        bool isCollectionItem = false) => new()
    {
        Key = "key",
        Label = label,
        Class = changeClass,
        ChangeType = changeType,
        IsCollectionItem = isCollectionItem,
        Consequence = consequence
    };
}
