// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The consequence copy an administrator consents to has to be true, because it is the only thing standing between
/// them and a destructive change they cannot picture.
///
/// Connected System Object Types and Partitions both snapshot their selection as "selected", so the copy is chosen by
/// the snapshot node the flag hangs from. Deselecting either takes its objects out of management: the next Full Import
/// no longer returns them, so they are obsoleted and disconnected (#1474). The Object Type's copy says so in its own
/// words, because an Object Type used to be the one selection that took nothing out of scope and the copy said that.
/// See <c>DeselectedObjectTypeDeletionDetectionTests</c>, which pins the behaviour the copy describes.
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
    public void For_DeselectingAnObjectType_PromisesObsoletionOnTheNextFullImport()
    {
        var consequence = ConfigurationChangeConsequences.For(
            ConfigurationSnapshotService.ConnectedSystemObjectType, ObjectTypeNode, SelectedKey, True, False);

        Assert.That(consequence, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(consequence, Does.Contain("become obsolete"),
                "deletion detection now walks deselected Object Types, so their objects are obsoleted as missing. " +
                "The administrator is consenting to that cascade and has to be told it is coming.");
            Assert.That(consequence, Does.Contain("Full Import"),
                "and when: nothing happens on save, it happens on the next Full Import, which is when a preview or a " +
                "Run Profile's deletion limits can still stop it.");
            Assert.That(consequence, Does.Contain("disconnect"),
                "the objects are disconnected from their Metaverse Objects, which is the part with consequences beyond " +
                "this Connected System.");
            Assert.That(consequence, Does.Not.Contain("does nothing else"),
                "the old copy described a freeze that no longer happens.");
        }
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
