// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Whether an object falls within a container's scope is the Connector's knowledge, not the framework's, and three
/// features depend on the same answer: the import builds its search scope from it, export refuses to write outside
/// the selected containers (#1250), and the partition and container deselection preview counts the objects a
/// deselection would take out of import scope (#1251). Copies of the rule would let a preview state a count that
/// export then disagreed with, so there is one predicate and all three go through it.
///
/// Container Scope (#351) is part of the question, not a detail beneath it: beneath a One Level container an import
/// returns nothing, so an implementation that assumed a subtree would report objects as managed that no import will
/// ever bring back.
/// </summary>
[TestFixture]
public class LdapConnectorContainmentTests
{
    private LdapConnector _connector = null!;
    private IConnectorContainment _containment = null!;

    [SetUp]
    public void SetUp()
    {
        _connector = new LdapConnector();
        _containment = _connector;
    }

    [TearDown]
    public void TearDown()
    {
        _connector.Dispose();
    }

    [Test]
    public void IsWithinContainer_ObjectDirectlyBeneathContainer_IsWithin()
    {
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Users,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.True);
    }

    [Test]
    public void IsWithinContainer_ObjectSeveralLevelsBeneathContainer_IsWithin()
    {
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Finance,OU=Users,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.True);
    }

    [Test]
    public void IsWithinContainer_ContainerItself_IsWithin()
    {
        // Selecting a container selects the container as well as its subtree; an object whose identifier *is* the
        // container is in scope, which is what makes the container's own entry importable.
        Assert.That(_containment.IsWithinContainer("OU=Users,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.True);
    }

    [Test]
    public void IsWithinContainer_SiblingContainerWithSharedNamePrefix_IsNotWithin()
    {
        // The reason containment compares components rather than characters: OU=UsersArchive is a different
        // container from OU=Users, and a substring match would silently include everything in it.
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=UsersArchive,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.False);
    }

    [Test]
    public void IsWithinContainer_ObjectElsewhereInTheDirectory_IsNotWithin()
    {
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Contractors,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.False);
    }

    [Test]
    public void IsWithinContainer_ContainerBeneathTheObject_IsNotWithin()
    {
        // Containment is one-directional. A parent is not within its own child.
        Assert.That(_containment.IsWithinContainer("OU=Users,DC=example,DC=com", Container("OU=Finance,OU=Users,DC=example,DC=com")), Is.False);
    }

    [Test]
    public void IsWithinContainer_DifferingCase_IsWithin()
    {
        // Directories compare Distinguished Names case-insensitively, and the same entry is routinely returned with
        // different casing by different servers; treating that as a different container would drop a selection.
        Assert.That(_containment.IsWithinContainer("cn=Jane Doe,ou=users,dc=example,dc=com", Container("OU=Users,DC=example,DC=com")), Is.True);
    }

    [Test]
    public void IsWithinContainer_WhitespaceAfterComponentSeparators_IsWithin()
    {
        // RFC 4514 permits optional whitespace around the separator, and directories emit it inconsistently. This
        // is the case a character-level suffix match gets wrong, refusing an export to an object that is plainly
        // inside the selected container.
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe, OU=Users, DC=example, DC=com", Container("OU=Users,DC=example,DC=com")), Is.True);
    }

    [Test]
    public void IsWithinContainer_EscapedCommaInsideAValue_DoesNotSplitTheName()
    {
        // "CN=Doe\, Jane" is one component, not two. Splitting on the raw comma would compare the wrong components
        // and could place the object in a container it is not in.
        Assert.That(_containment.IsWithinContainer(@"CN=Doe\, Jane,OU=Users,DC=example,DC=com", Container("OU=Users,DC=example,DC=com")), Is.True);
        Assert.That(_containment.IsWithinContainer(@"CN=Doe\, Jane,OU=Users,DC=example,DC=com", Container(@"Jane,OU=Users,DC=example,DC=com")), Is.False);
    }

    [Test]
    public void IsWithinContainer_EmptyOrMalformedIdentifiers_AreNotWithin()
    {
        // A preview counting objects, and an export deciding whether to write, both need a definite answer here.
        // "Not in scope" is the safe one: it under-counts a preview and refuses a write, rather than claiming an
        // object is somewhere it may not be.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_containment.IsWithinContainer("", Container("OU=Users,DC=example,DC=com")), Is.False);
            Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Users,DC=example,DC=com", Container("")), Is.False);
            Assert.That(_containment.IsWithinContainer("not a distinguished name", Container("OU=Users,DC=example,DC=com")), Is.False);
            Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Users,DC=example,DC=com", Container("not a distinguished name")), Is.False);
        }
    }

    // ─── Container Scope (#351) ───

    [Test]
    public void IsWithinContainer_OneLevelContainer_AdmitsObjectsDirectlyWithinIt()
    {
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Users,DC=example,DC=com",
            Container("OU=Users,DC=example,DC=com", ConnectedSystemContainerScope.OneLevel)), Is.True);
    }

    [Test]
    public void IsWithinContainer_OneLevelContainer_ExcludesObjectsFurtherDown()
    {
        // The case that makes scope part of the question rather than a detail beneath it. A One Level import
        // returns nothing from OU=Finance, so treating this object as managed would have a preview count it as
        // in scope today and an export write to somewhere the next import cannot read.
        Assert.That(_containment.IsWithinContainer("CN=Jane Doe,OU=Finance,OU=Users,DC=example,DC=com",
            Container("OU=Users,DC=example,DC=com", ConnectedSystemContainerScope.OneLevel)), Is.False);
    }

    [Test]
    public void IsWithinContainer_OneLevelContainer_ExcludesTheContainerItself()
    {
        // A one-level search returns the entries within its base, not the base entry; a subtree search returns both.
        Assert.That(_containment.IsWithinContainer("OU=Users,DC=example,DC=com",
            Container("OU=Users,DC=example,DC=com", ConnectedSystemContainerScope.OneLevel)), Is.False);
    }

    [Test]
    public void IsWithinContainer_OneLevelContainer_TreatsAnEscapedCommaAsPartOfOneName()
    {
        // "CN=Doe\, Jane" is a single component, so this object is directly within OU=Users and a One Level
        // search returns it. Counting the escaped comma as a separator would place it a level too deep.
        Assert.That(_containment.IsWithinContainer(@"CN=Doe\, Jane,OU=Users,DC=example,DC=com",
            Container("OU=Users,DC=example,DC=com", ConnectedSystemContainerScope.OneLevel)), Is.True);
    }

    private static ConnectedSystemContainer Container(
        string externalId,
        ConnectedSystemContainerScope scope = ConnectedSystemContainerScope.Subtree) =>
        new() { ExternalId = externalId, Scope = scope };
}
