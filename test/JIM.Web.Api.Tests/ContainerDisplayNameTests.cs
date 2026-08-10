// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Shortening a Container's name against its partition (#1275). The risk in this is not the common case but the
/// ones where trimming would be wrong: a Connector that does not name containers hierarchically, and a container
/// whose name is the partition's own.
/// </summary>
[TestFixture]
public class ContainerDisplayNameTests
{
    [Test]
    public void RelativeTo_ContainerBeneathThePartition_DropsThePartitionSuffix()
    {
        Assert.That(ContainerDisplayName.RelativeTo("ou=Contractors,dc=corp,dc=local", "dc=corp,dc=local"),
            Is.EqualTo("ou=Contractors"));
    }

    [Test]
    public void RelativeTo_NestedContainer_KeepsEverythingAboveThePartition()
    {
        // The path within the partition is what tells one nested container from another, so only the shared suffix
        // goes; the row still says which branch it is on.
        Assert.That(ContainerDisplayName.RelativeTo("ou=Finance,ou=Users,dc=corp,dc=local", "dc=corp,dc=local"),
            Is.EqualTo("ou=Finance,ou=Users"));
    }

    [Test]
    public void RelativeTo_DifferingCase_StillMatches()
    {
        // Directories compare Distinguished Names case-insensitively and return the same suffix cased differently
        // at different depths. A case-sensitive match would shorten some rows in one list and not others.
        Assert.That(ContainerDisplayName.RelativeTo("OU=Contractors,DC=Corp,DC=Local", "dc=corp,dc=local"),
            Is.EqualTo("OU=Contractors"));
    }

    [Test]
    public void RelativeTo_NameThatDoesNotEndInThePartition_IsLeftAlone()
    {
        // A Connector that does not name containers hierarchically (a folder path, a table name) must not be
        // silently truncated because a suffix happened not to match.
        Assert.That(ContainerDisplayName.RelativeTo("/exports/daily", "dc=corp,dc=local"),
            Is.EqualTo("/exports/daily"));
    }

    [Test]
    public void RelativeTo_ContainerThatIsThePartition_KeepsItsOwnName()
    {
        // Trimming leaves nothing at all here, and a blank row is worse than a repeated suffix.
        Assert.That(ContainerDisplayName.RelativeTo("dc=corp,dc=local", "dc=corp,dc=local"),
            Is.EqualTo("dc=corp,dc=local"));
    }

    [Test]
    public void RelativeTo_NoPartitionName_LeavesTheContainerAlone()
    {
        Assert.That(ContainerDisplayName.RelativeTo("ou=Contractors,dc=corp,dc=local", null),
            Is.EqualTo("ou=Contractors,dc=corp,dc=local"));
    }

    [Test]
    public void RelativeTo_NoContainerName_IsEmpty()
    {
        Assert.That(ContainerDisplayName.RelativeTo(null, "dc=corp,dc=local"), Is.Empty);
    }
}
