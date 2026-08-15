// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.LDAP;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The search a Container object count runs (#1276). The filter is the part that decides whether the figure an
/// administrator reads matches what a Full Import would actually bring back.
/// </summary>
[TestFixture]
public class LdapConnectorContainerCountsTests
{
    [Test]
    public void BuildObjectClassFilter_OneObjectType_IsNotWrappedInARedundantOr()
    {
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["user"]), Is.EqualTo("(objectClass=user)"));
    }

    [Test]
    public void BuildObjectClassFilter_SeveralObjectTypes_MatchesAnyOfThem()
    {
        // One search over the union costs one pass over the directory; one search per Object Type costs a pass
        // each, and the counts have to be merged afterwards anyway.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["user", "group"]),
            Is.EqualTo("(|(objectClass=user)(objectClass=group))"));
    }

    [Test]
    public void BuildObjectClassFilter_TheSameTypesAsAFullImport_ProducesTheUnionOfItsFilters()
    {
        // A Full Import searches (objectClass={type}) per selected Object Type. The count has to match that set
        // exactly, or it reports a number the next import contradicts.
        var filter = LdapConnectorContainerCounts.BuildObjectClassFilter(["user", "group", "contact"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter, Does.Contain("(objectClass=user)"));
            Assert.That(filter, Does.Contain("(objectClass=group)"));
            Assert.That(filter, Does.Contain("(objectClass=contact)"));
            Assert.That(filter, Does.StartWith("(|"));
        }
    }

    [TestCase("weird(name", "(objectClass=weird\\28name)")]
    [TestCase("weird)name", "(objectClass=weird\\29name)")]
    [TestCase("weird*name", "(objectClass=weird\\2aname)")]
    [TestCase("weird\\name", "(objectClass=weird\\5cname)")]
    public void BuildObjectClassFilter_AnObjectTypeCarryingAReservedCharacter_IsEscaped(string objectTypeName, string expected)
    {
        // Object Type names come from the directory's own schema rather than from a person, so this is not a
        // user-input injection route. It is still a filter built by concatenation, and one schema carrying a
        // parenthesis would otherwise produce a malformed filter or a search that matches the wrong thing.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter([objectTypeName]), Is.EqualTo(expected));
    }

    [Test]
    public void ShouldStopForBudget_WellInsideTheBudget_KeepsGoing()
    {
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)), Is.False);
    }

    [Test]
    public void ShouldStopForBudget_PastTheBudget_Stops()
    {
        // Counting is folded into Retrieve Hierarchy, so it is spending an administrator's wait on something they
        // did not ask for by name. The hierarchy is the thing they wanted; the count is not allowed to hold it
        // hostage indefinitely on a large directory.
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(30)), Is.True);
    }

    [Test]
    public void ShouldStopForBudget_ExactlyOnTheBudget_Stops()
    {
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), Is.True);
    }

    [Test]
    public void ShouldStopForBudget_NoBudgetSet_NeverStops()
    {
        // Zero or less means "no budget", which is what an unattended caller wants; the cancellation token is then
        // the only thing that stops it.
        Assert.That(LdapConnectorContainerCounts.ShouldStopForBudget(TimeSpan.FromHours(2), TimeSpan.Zero), Is.False);
    }

    [Test]
    public void BuildObjectClassFilter_ABackslashInAName_IsEscapedOnceNotTwice()
    {
        // The backslash must be escaped before the characters whose escapes introduce backslashes of their own,
        // or "\\" becomes "\\5c5c" and the filter no longer means what it says.
        Assert.That(LdapConnectorContainerCounts.BuildObjectClassFilter(["a\\*b"]), Is.EqualTo("(objectClass=a\\5c\\2ab)"));
    }
}
