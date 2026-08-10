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

    #region Helper Methods

    private static ConnectedSystemContainer Container(string externalId, ConnectedSystemContainerScope scope) =>
        new() { Name = externalId, ExternalId = externalId, Selected = true, Scope = scope };

    private static bool IsWithin(string? objectIdentifier, ConnectedSystemContainer container) =>
        DistinguishedNameContainment.Instance.IsWithinContainer(objectIdentifier, container);

    #endregion
}
