// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The consequence copy an administrator consents to has to be true, because it is the only thing standing between
/// them and a destructive change they cannot picture.
///
/// Connected System Object Types and Partitions both snapshot their selection as "selected", and the copy for the
/// pair was written once, for the Partition. The two do not behave alike: deselecting a Partition leaves its objects
/// missing from an import that still runs, so they are obsoleted and deprovisioned, whereas deselecting an Object
/// Type removes it from deletion detection altogether (<c>ObjectTypes.Where(ot =&gt; ot.Selected)</c>), so its objects
/// are never even looked at. See <c>DeselectedObjectTypeDeletionDetectionTests</c>, which pins that behaviour.
/// </summary>
[TestFixture]
public class ConfigurationChangeConsequenceTests
{
    private const string ObjectTypeNode = "objectType";
    private const string PartitionNode = "partition";
    private const string SelectedKey = "selected";
    private const string True = "true";
    private const string False = "false";

    [Test]
    public void For_DeselectingAnObjectType_DoesNotPromiseObsoletion()
    {
        var consequence = ConfigurationChangeConsequences.For(
            ConfigurationSnapshotService.ConnectedSystemObjectType, ObjectTypeNode, SelectedKey, True, False);

        Assert.That(consequence, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(consequence, Does.Not.Contain("become obsolete"),
                "deletion detection skips deselected Object Types, so their objects are never compared against the " +
                "import and never obsoleted. Promising a cascade that never happens teaches the administrator that " +
                "the type is out of management when its objects are still joined and still contributing.");
            Assert.That(consequence, Does.Contain("Nothing is obsoleted and nothing is deprovisioned"),
                "and the denial has to be explicit, because the administrator has just been told the change is " +
                "destructive and will otherwise assume the usual cascade");
        }
    }

    [Test]
    public void For_DeselectingAnObjectType_SaysItsObjectsAreLeftInPlaceAndStale()
    {
        var consequence = ConfigurationChangeConsequences.For(
            ConfigurationSnapshotService.ConnectedSystemObjectType, ObjectTypeNode, SelectedKey, True, False);

        Assert.That(consequence, Does.Contain("joined"),
            "the objects stay joined to their Metaverse Objects, which is the part the administrator cannot see");
        Assert.That(consequence, Does.Contain("contribut"),
            "and they keep contributing the values they last imported, which is what makes the freeze dangerous");
    }

    [Test]
    public void For_DeselectingAPartition_StillPromisesObsoletion()
    {
        // The control. The copy was correct for Partitions all along, and separating the two must not weaken it:
        // a deselected Partition's objects genuinely are missing from an import that still covers them.
        var consequence = ConfigurationChangeConsequences.For(
            ConfigurationSnapshotService.ConnectedSystemObjectType, PartitionNode, SelectedKey, True, False);

        Assert.That(consequence, Does.Contain("obsolete").IgnoreCase);
        Assert.That(consequence, Does.Contain("deprovision").IgnoreCase);
    }

    [Test]
    public void For_SelectingEither_ReadsAsBringingObjectsIntoScope()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationChangeConsequences.For(
                    ConfigurationSnapshotService.ConnectedSystemObjectType, ObjectTypeNode, SelectedKey, False, True),
                Does.Contain("import"));
            Assert.That(ConfigurationChangeConsequences.For(
                    ConfigurationSnapshotService.ConnectedSystemObjectType, PartitionNode, SelectedKey, False, True),
                Does.Contain("import"));
        }
    }

    [Test]
    public void HasCopyFor_SelectionWithNoParentContext_StillReportsCopyExists()
    {
        // The completeness test asks only whether a destructive key is explained at all, with no tree to read a
        // parent from. Splitting the copy by parent must not make the key look uncovered.
        Assert.That(ConfigurationChangeConsequences.HasCopyFor(
            ConfigurationSnapshotService.ConnectedSystemObjectType, SelectedKey), Is.True);
    }
}
