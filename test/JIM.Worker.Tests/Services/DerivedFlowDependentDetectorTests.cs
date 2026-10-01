// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// FR 3 of Metaverse-Derived Attribute Flows (#1750): the derived flows a configuration change leaves with a missing
/// input, directly or transitively, because an attribute they read loses its last enabled contributor. Pure, after the
/// <see cref="SchemaRefreshDependentDetector"/> pattern; each test hands it the rules before and after a change.
/// </summary>
[TestFixture]
public class DerivedFlowDependentDetectorTests
{
    private DerivedFlowTestModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
    }

    private IReadOnlyList<DerivedFlowDependent> Detect(SyncRule[] before, SyncRule[] after) =>
        DerivedFlowDependentDetector.Detect(before, after, _model.Types);

    [Test]
    public void Detect_LastContributorOfAnInputRemoved_ReportsTheDerivedFlowReadingItDirectly()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var email = Expression(ad, 201, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var dependents = Detect([hr, ad], [Without(hr, accountName), ad]);

        Assert.That(dependents, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents[0].Flow.Mapping, Is.SameAs(email));
            Assert.That(dependents[0].Flow.SyncRule.Name, Is.EqualTo("AD Import"));
            Assert.That(dependents[0].MissingInputs.Single().MetaverseAttributeName, Is.EqualTo("Account Name"));
            Assert.That(dependents[0].MissingInputs.Single().ThroughDerivedFlows, Is.False);
        }
    }

    [Test]
    public void Detect_ChainOfDerivedFlows_ReportsEveryFlowDownTheChainTransitively()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var email = Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");
        var upn = Expression(hr, 103, _model.UserPrincipalName, "mv[\"Email\"]");

        var dependents = Detect([hr], [Without(hr, accountName)]);

        Assert.That(dependents.Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email, upn }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependents[0].MissingInputs.Single().ThroughDerivedFlows, Is.False, "Email reads Account Name, which has no contributor left");
            Assert.That(dependents[1].MissingInputs.Single().MetaverseAttributeName, Is.EqualTo("Email"));
            Assert.That(dependents[1].MissingInputs.Single().ThroughDerivedFlows, Is.True,
                "Email keeps its derived contributor, but that contributor is itself missing its input");
        }
    }

    [Test]
    public void Detect_ChainAttributeKeepsAContributorWithItsInputs_StopsTheChainThere()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var email = Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");
        Expression(hr, 103, _model.UserPrincipalName, "mv[\"Email\"]");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        Direct(ad, 201, _model.Email, "mail");

        var dependents = Detect([hr, ad], [Without(hr, accountName), ad]);

        Assert.That(dependents.Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }),
            "AD still contributes Email directly, so User Principal Name keeps a value to read");
    }

    [Test]
    public void Detect_AnotherEnabledContributorRemains_ReportsNothing()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        Direct(ad, 201, _model.AccountName, "sAMAccountName");
        Expression(ad, 202, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        Assert.That(Detect([hr, ad], [Without(hr, accountName), ad]), Is.Empty);
    }

    [Test]
    public void Detect_RemainingContributorIsADisabledMapping_StillReportsTheDependant()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        Direct(ad, 201, _model.AccountName, "sAMAccountName").Enabled = false;
        var email = Expression(ad, 202, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var dependents = Detect([hr, ad], [Without(hr, accountName), ad]);

        Assert.That(dependents.Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }), "a disabled mapping contributes nothing");
    }

    [Test]
    public void Detect_RemainingContributorIsOnADisabledRule_StillReportsTheDependant()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var training = ImportRule(3, "Training Import", connectedSystemId: 3, enabled: false);
        Direct(training, 301, _model.AccountName, "login");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var email = Expression(ad, 202, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var dependents = Detect([hr, training, ad], [Without(hr, accountName), training, ad]);

        Assert.That(dependents.Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }), "a disabled rule contributes nothing");
    }

    [Test]
    public void Detect_ContributorDisabledRatherThanRemoved_ReportsTheDependant()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var email = Expression(ad, 202, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var hrAfter = Without(hr, accountName);
        var disabled = Direct(hrAfter, 101, _model.AccountName, "sAMAccountName");
        disabled.Enabled = false;

        Assert.That(Detect([hr, ad], [hrAfter, ad]).Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }));
    }

    [Test]
    public void Detect_WholeContributingRuleDisabled_ReportsTheDependant()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var email = Expression(ad, 202, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var hrAfter = Without(hr, null);
        hrAfter.Enabled = false;

        Assert.That(Detect([hr, ad], [hrAfter, ad]).Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }));
    }

    [Test]
    public void Detect_InputHadNoContributorBeforeTheChange_ReportsNothing()
    {
        // Nothing contributes Region either side of the change: the derived flow was already missing it, so this
        // change is not what caused it.
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var displayName = Direct(hr, 101, _model.DisplayName, "displayName");
        Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");

        Assert.That(Detect([hr], [Without(hr, displayName)]), Is.Empty);
    }

    [Test]
    public void Detect_SameRuleHostsTheDerivedFlowAndTheRemovedContributor_ReportsIt()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var email = Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var dependents = Detect([hr], [Without(hr, accountName)]);

        Assert.That(dependents.Select(d => d.Flow.Mapping), Is.EqualTo(new[] { email }));
        Assert.That(dependents[0].Flow.SyncRule.Name, Is.EqualTo("HR Import"));
    }

    [Test]
    public void Detect_DerivedFlowRemovedAlongWithItsInput_ReportsNothing()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        Direct(hr, 101, _model.AccountName, "sAMAccountName");
        Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var hrAfter = Without(hr, null);
        hrAfter.AttributeFlowRules.Clear();

        Assert.That(Detect([hr], [hrAfter]), Is.Empty, "a flow that will not run has no missing input to report");
    }

    [Test]
    public void Detect_DerivedFlowReadingTwoInputs_NamesOnlyTheOneLeftWithoutAContributor()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        Direct(hr, 102, _model.Region, "region");
        Expression(hr, 103, _model.Email, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");

        var dependents = Detect([hr], [Without(hr, accountName)]);

        Assert.That(dependents.Single().MissingInputs.Select(i => i.MetaverseAttributeName), Is.EqualTo(new[] { "Account Name" }));
    }

    [Test]
    public void Detect_NoDerivedFlows_ReportsNothing()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        Direct(hr, 102, _model.Email, "mail");

        Assert.That(Detect([hr], [Without(hr, accountName)]), Is.Empty);
    }

    [Test]
    public void Detect_SameAttributeNameOnAnotherMetaverseObjectType_IsNotAContributor()
    {
        // Email is an attribute of both Person and Group; a Group rule contributing it keeps nothing alive for Person.
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Direct(hr, 101, _model.Email, "mail");
        var upn = Expression(hr, 102, _model.UserPrincipalName, "mv[\"Email\"]");
        var groups = ImportRule(4, "Group Import", connectedSystemId: 4, metaverseObjectTypeId: GroupTypeId);
        Direct(groups, 401, _model.Email, "mail");

        Assert.That(Detect([hr, groups], [Without(hr, email), groups]).Select(d => d.Flow.Mapping), Is.EqualTo(new[] { upn }));
    }

    [Test]
    public void Detect_DirectDependant_NamesTheInputThatLostItsContributorWithNoVia()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");

        var lost = Detect([hr], [Without(hr, accountName)]).Single().LostInputs;

        Assert.That(lost, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lost[0].MetaverseAttributeName, Is.EqualTo("Account Name"));
            Assert.That(lost[0].MetaverseAttributeId, Is.EqualTo(_model.AccountName.Id));
            Assert.That(lost[0].Indirect, Is.False);
            Assert.That(lost[0].Via, Is.Empty);
        }
    }

    [Test]
    public void Detect_TransitiveDependant_NamesTheRootInputAndTheChainItIsReachedThrough()
    {
        // User Principal Name reads Display Name, which reads Email, which reads Account Name. Removing Account Name's
        // only contributor reaches User Principal Name through Display Name, then Email.
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        Expression(hr, 102, _model.Email, "mv[\"Account Name\"] + \"@corp\"");
        Expression(hr, 103, _model.DisplayName, "mv[\"Email\"]");
        var upn = Expression(hr, 104, _model.UserPrincipalName, "mv[\"Display Name\"]");

        var dependant = Detect([hr], [Without(hr, accountName)]).Single(d => ReferenceEquals(d.Flow.Mapping, upn));

        Assert.That(dependant.LostInputs, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependant.LostInputs[0].MetaverseAttributeName, Is.EqualTo("Account Name"));
            Assert.That(dependant.LostInputs[0].Indirect, Is.True);
            Assert.That(dependant.LostInputs[0].Via, Is.EqualTo(new[] { "Display Name", "Email" }),
                "the chain reads from the flow's own input towards the attribute that lost its contributor");
        }
    }

    [Test]
    public void Detect_DependantStarvedThroughTwoRoots_NamesEachRootOnce()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var accountName = Direct(hr, 101, _model.AccountName, "sAMAccountName");
        var region = Direct(hr, 102, _model.Region, "region");
        Expression(hr, 103, _model.Email, "mv[\"Account Name\"] + \"@\" + mv[\"Region\"]");
        var upn = Expression(hr, 104, _model.UserPrincipalName, "mv[\"Email\"] + mv[\"Region\"]");
        var hrAfter = Without(hr, accountName);
        hrAfter.AttributeFlowRules.Remove(region);

        var dependant = Detect([hr], [hrAfter]).Single(d => ReferenceEquals(d.Flow.Mapping, upn));

        Assert.That(dependant.LostInputs.Select(i => (i.MetaverseAttributeName, i.Indirect, string.Join(">", i.Via))),
            Is.EqualTo(new[] { ("Region", false, ""), ("Account Name", true, "Email") }),
            "nearest first, each root once at its shortest chain (Region is read directly as well as through Email)");
    }

    /// <summary>
    /// A copy of <paramref name="rule"/> (same id, name and settings, the same mapping instances) without
    /// <paramref name="removed"/>, standing for the rule after a change.
    /// </summary>
    private static SyncRule Without(SyncRule rule, SyncRuleMapping? removed)
    {
        var copy = ImportRule(rule.Id, rule.Name, rule.ConnectedSystemId, rule.MetaverseObjectTypeId, rule.Enabled);
        copy.AttributeFlowRules.AddRange(rule.AttributeFlowRules.Where(mapping => !ReferenceEquals(mapping, removed)));
        return copy;
    }
}
