// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Which of several Containers has the final say over an object.
/// </summary>
/// <remarks>
/// Membership used to be an OR across the selected Containers, which answers "is this object in scope?" and nothing
/// else. An exclusion (#1255) needs the Container that is *most specific* about the object, because the including
/// ancestor always matches and would always win an OR. These tests pin the ranking rule on its own, before anything
/// depends on it.
/// </remarks>
[TestFixture]
public class ContainerSpecificityTests
{
    [Test]
    public void ResolveMostSpecific_NoContainers_ReturnsNull()
    {
        Assert.That(ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Corp,DC=example,DC=local", [], IsWithin), Is.Null);
    }

    [Test]
    public void ResolveMostSpecific_TheOnlyContainerMatches_ReturnsIt()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Corp,DC=example,DC=local", [corp], IsWithin);

        Assert.That(result, Is.SameAs(corp));
    }

    [Test]
    public void ResolveMostSpecific_TheOnlyContainerDoesNotMatch_ReturnsNull()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Partners,DC=example,DC=local", [corp], IsWithin);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveMostSpecific_NestedSubtreeContainers_ReturnsTheDeeperOne()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var sales = Container("OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local", [corp, sales], IsWithin);

        Assert.That(result, Is.SameAs(sales));
    }

    [Test]
    public void ResolveMostSpecific_NestedSubtreeContainersSuppliedDeepestFirst_ReturnsTheSameOne()
    {
        // The answer is a property of the Containers, not of the order a caller happened to collect them in.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var sales = Container("OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local", [sales, corp], IsWithin);

        Assert.That(result, Is.SameAs(sales));
    }

    [Test]
    public void ResolveMostSpecific_ThreeLevelsOfNesting_ReturnsTheDeepestOne()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var sales = Container("OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var emea = Container("OU=EMEA,OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=EMEA,OU=Sales,OU=Corp,DC=example,DC=local", [corp, sales, emea], IsWithin);

        Assert.That(result, Is.SameAs(emea));
    }

    [Test]
    public void ResolveMostSpecific_ObjectAboveTheDeeperContainer_ReturnsTheAncestor()
    {
        // Only Corp admits an object held directly in Corp, so Corp is both the only match and the most specific.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var sales = Container("OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Corp,DC=example,DC=local", [corp, sales], IsWithin);

        Assert.That(result, Is.SameAs(corp));
    }

    [Test]
    public void ResolveMostSpecific_OneLevelAncestorAndSubtreeDescendant_ReturnsTheOneMatchingContainer()
    {
        // A OneLevel Container and a Container beneath it never admit the same object, so no ranking is needed;
        // this pins that the OneLevel ancestor does not steal an object that belongs to the descendant.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.OneLevel);
        var sales = Container("OU=Sales,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Sales,OU=Corp,DC=example,DC=local", [corp, sales], IsWithin), Is.SameAs(sales));
            Assert.That(ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Corp,DC=example,DC=local", [corp, sales], IsWithin), Is.SameAs(corp));
        }
    }

    [Test]
    public void ResolveMostSpecific_ContainersInDisjointBranches_ReturnsTheMatchingOne()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var partners = Container("OU=Partners,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=External,OU=Partners,DC=example,DC=local", [corp, partners], IsWithin);

        Assert.That(result, Is.SameAs(partners));
    }

    [Test]
    public void ResolveMostSpecific_ObjectOutsideEveryContainer_ReturnsNull()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var partners = Container("OU=Partners,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        var result = ContainerSpecificity.ResolveMostSpecific("CN=Alice,OU=Contractors,DC=example,DC=local", [corp, partners], IsWithin);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveMostSpecific_NullIdentifier_ReturnsNull()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        Assert.That(ContainerSpecificity.ResolveMostSpecific(null, [corp], IsWithin), Is.Null);
    }

    #region IsInScope tests

    // The decision the ranking exists to serve (#1255). Ranking on its own says which Container decides; this says
    // what that Container's decision is, which is the question every import, export and preview actually asks.

    [Test]
    public void IsInScope_NoContainers_ReturnsFalse()
    {
        // No Container admits the object, so nothing puts it in scope. A caller with no Container-level opinion to
        // apply at all does not ask this question; it never narrows scope in the first place.
        Assert.That(ContainerSpecificity.IsInScope("CN=Alice,OU=Corp,DC=example,DC=local", [], IsWithin), Is.False);
    }

    [Test]
    public void IsInScope_ObjectOutsideEveryContainer_ReturnsFalse()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        Assert.That(ContainerSpecificity.IsInScope("CN=Alice,OU=Partners,DC=example,DC=local", [corp], IsWithin), Is.False);
    }

    [Test]
    public void IsInScope_TheDecidingContainerIsSelected_ReturnsTrue()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        Assert.That(ContainerSpecificity.IsInScope("CN=Alice,OU=Corp,DC=example,DC=local", [corp], IsWithin), Is.True);
    }

    [Test]
    public void IsInScope_ObjectWithinAnExcludedBranchOfASelectedParent_ReturnsFalse()
    {
        // The case the whole of #1255 exists for: manage Corp, except its Service Accounts.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var serviceAccounts = ExcludedContainer("OU=Service Accounts,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSpecificity.IsInScope("CN=svc-backup,OU=Service Accounts,OU=Corp,DC=example,DC=local", [corp, serviceAccounts], IsWithin), Is.False);
            Assert.That(ContainerSpecificity.IsInScope("CN=Alice,OU=Corp,DC=example,DC=local", [corp, serviceAccounts], IsWithin), Is.True);
        }
    }

    [Test]
    public void IsInScope_ExclusionSuppliedBeforeTheSelectionItSitsWithin_ReturnsTheSameAnswer()
    {
        // The answer is a property of the Containers, not of the order a caller happened to collect them in.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var serviceAccounts = ExcludedContainer("OU=Service Accounts,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        Assert.That(ContainerSpecificity.IsInScope("CN=svc-backup,OU=Service Accounts,OU=Corp,DC=example,DC=local", [serviceAccounts, corp], IsWithin), Is.False);
    }

    [Test]
    public void IsInScope_SelectionBeneathAnExclusion_ReturnsTrue()
    {
        // Re-inclusion to arbitrary depth is what most-specific-match buys, with no further machinery.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var serviceAccounts = ExcludedContainer("OU=Service Accounts,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var app1 = Container("OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSpecificity.IsInScope("CN=svc-app1,OU=App1,OU=Service Accounts,OU=Corp,DC=example,DC=local", [corp, serviceAccounts, app1], IsWithin), Is.True);
            Assert.That(ContainerSpecificity.IsInScope("CN=svc-backup,OU=Service Accounts,OU=Corp,DC=example,DC=local", [corp, serviceAccounts, app1], IsWithin), Is.False);
        }
    }

    [Test]
    public void IsInScope_ExcludedOneLevelContainer_CarvesOutOnlyItsOwnLevel()
    {
        // A Container's Scope says how far its statement reaches, whether that statement is a selection or an
        // exclusion. A One Level exclusion says nothing about what sits beneath it, so the selected ancestor still
        // governs there.
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);
        var staging = ExcludedContainer("OU=Staging,OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.OneLevel);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSpecificity.IsInScope("CN=Temp,OU=Staging,OU=Corp,DC=example,DC=local", [corp, staging], IsWithin), Is.False);
            Assert.That(ContainerSpecificity.IsInScope("CN=Temp,OU=Batch,OU=Staging,OU=Corp,DC=example,DC=local", [corp, staging], IsWithin), Is.True);
        }
    }

    [Test]
    public void IsInScope_TwoEquallySpecificContainersDisagree_ReturnsFalse()
    {
        // Containment that is not a tree can admit an object into two Containers neither of which holds the other,
        // and ranking has no answer between them. Where they disagree the exclusion decides: importing an object an
        // administrator excluded is the worse of the two failures, and "whichever we saw first" is not an answer to
        // "is this object managed?" at all.
        var selected = Container("first", ConnectedSystemContainerScope.Subtree);
        var excluded = ExcludedContainer("second", ConnectedSystemContainerScope.Subtree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerSpecificity.IsInScope("object", [selected, excluded], AdmitsEverything), Is.False);
            Assert.That(ContainerSpecificity.IsInScope("object", [excluded, selected], AdmitsEverything), Is.False);
        }
    }

    [Test]
    public void IsInScope_NullIdentifier_ReturnsFalse()
    {
        var corp = Container("OU=Corp,DC=example,DC=local", ConnectedSystemContainerScope.Subtree);

        Assert.That(ContainerSpecificity.IsInScope(null, [corp], IsWithin), Is.False);
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystemContainer Container(string externalId, ConnectedSystemContainerScope scope) =>
        new() { Name = externalId, ExternalId = externalId, Selected = true, Scope = scope };

    private static ConnectedSystemContainer ExcludedContainer(string externalId, ConnectedSystemContainerScope scope) =>
        new() { Name = externalId, ExternalId = externalId, Excluded = true, Scope = scope };

    private static bool IsWithin(string? objectIdentifier, ConnectedSystemContainer container) =>
        DistinguishedNameContainment.Instance.IsWithinContainer(objectIdentifier, container);

    /// <summary>
    /// Containment that is not a hierarchy: every Container admits everything, including the other Containers, so
    /// no Container is more specific than any other.
    /// </summary>
    private static bool AdmitsEverything(string? objectIdentifier, ConnectedSystemContainer container) => true;

    #endregion
}
