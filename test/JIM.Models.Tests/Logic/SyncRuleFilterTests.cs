// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

[TestFixture]
public class SyncRuleFilterTests
{
    private static SyncRuleHeader ImportRule(
        int id = 1,
        string name = "HR Inbound",
        int connectedSystemId = 1,
        bool projectToMetaverse = true,
        bool enabled = true,
        string? description = null)
    {
        return new SyncRuleHeader
        {
            Id = id,
            Name = name,
            Description = description,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = "HR System",
            ConnectedSystemObjectTypeName = "Employee",
            MetaverseObjectTypeName = "Person",
            Direction = SyncRuleDirection.Import,
            ProjectToMetaverse = projectToMetaverse,
            ProvisionToConnectedSystem = null,
            Enabled = enabled
        };
    }

    private static SyncRuleHeader ExportRule(
        int id = 2,
        string name = "AD Outbound",
        int connectedSystemId = 2,
        bool provisionToConnectedSystem = true,
        bool enabled = true,
        string? description = null)
    {
        return new SyncRuleHeader
        {
            Id = id,
            Name = name,
            Description = description,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = "Contoso AD",
            ConnectedSystemObjectTypeName = "user",
            MetaverseObjectTypeName = "Person",
            Direction = SyncRuleDirection.Export,
            ProjectToMetaverse = null,
            ProvisionToConnectedSystem = provisionToConnectedSystem,
            Enabled = enabled
        };
    }

    #region Action type derivation

    [Test]
    public void GetActionType_ImportRuleThatProjects_ReturnsProjects()
    {
        Assert.That(SyncRuleFilter.GetActionType(ImportRule(projectToMetaverse: true)), Is.EqualTo(SyncRuleActionType.Projects));
    }

    [Test]
    public void GetActionType_ExportRuleThatProvisions_ReturnsProvisions()
    {
        Assert.That(SyncRuleFilter.GetActionType(ExportRule(provisionToConnectedSystem: true)), Is.EqualTo(SyncRuleActionType.Provisions));
    }

    [Test]
    public void GetActionType_ImportRuleThatDoesNotProject_ReturnsFlowOnly()
    {
        Assert.That(SyncRuleFilter.GetActionType(ImportRule(projectToMetaverse: false)), Is.EqualTo(SyncRuleActionType.FlowOnly));
    }

    [Test]
    public void GetActionType_ExportRuleThatDoesNotProvision_ReturnsFlowOnly()
    {
        Assert.That(SyncRuleFilter.GetActionType(ExportRule(provisionToConnectedSystem: false)), Is.EqualTo(SyncRuleActionType.FlowOnly));
    }

    [Test]
    public void GetActionType_ImportRuleWithNullProjectToMetaverse_ReturnsFlowOnly()
    {
        var header = ImportRule();
        header.ProjectToMetaverse = null;

        Assert.That(SyncRuleFilter.GetActionType(header), Is.EqualTo(SyncRuleActionType.FlowOnly));
    }

    [Test]
    public void GetActionType_ExportRuleWithProjectToMetaverseSet_ReturnsFlowOnly()
    {
        // ProjectToMetaverse is only meaningful for Import rules; an Export rule carrying a stale
        // value must not be reported as projecting.
        var header = ExportRule(provisionToConnectedSystem: false);
        header.ProjectToMetaverse = true;

        Assert.That(SyncRuleFilter.GetActionType(header), Is.EqualTo(SyncRuleActionType.FlowOnly));
    }

    #endregion

    #region Empty filter

    [Test]
    public void Matches_EmptyFilter_MatchesEverything()
    {
        var filter = new SyncRuleFilter();

        Assert.That(filter.Matches(ImportRule()), Is.True);
        Assert.That(filter.Matches(ExportRule(enabled: false)), Is.True);
    }

    [Test]
    public void IsEmpty_NoFacetsOrSearch_ReturnsTrue()
    {
        Assert.That(new SyncRuleFilter().IsEmpty, Is.True);
    }

    [Test]
    public void IsEmpty_WithEmptyCollections_ReturnsTrue()
    {
        var filter = new SyncRuleFilter
        {
            ConnectedSystemIds = [],
            Directions = [],
            ActionTypes = [],
            Statuses = [],
            Search = "   "
        };

        Assert.That(filter.IsEmpty, Is.True);
    }

    [Test]
    public void IsEmpty_WithASingleFacetValue_ReturnsFalse()
    {
        var filter = new SyncRuleFilter { Directions = [SyncRuleDirection.Import] };

        Assert.That(filter.IsEmpty, Is.False);
    }

    #endregion

    #region Individual facets

    [Test]
    public void Matches_ConnectedSystemFilter_OnlyMatchesRulesForThoseSystems()
    {
        var filter = new SyncRuleFilter { ConnectedSystemIds = [1] };

        Assert.That(filter.Matches(ImportRule(connectedSystemId: 1)), Is.True);
        Assert.That(filter.Matches(ExportRule(connectedSystemId: 2)), Is.False);
    }

    [Test]
    public void Matches_MultipleConnectedSystems_MatchesAnyOfThem()
    {
        var filter = new SyncRuleFilter { ConnectedSystemIds = [1, 2] };

        Assert.That(filter.Matches(ImportRule(connectedSystemId: 1)), Is.True);
        Assert.That(filter.Matches(ExportRule(connectedSystemId: 2)), Is.True);
        Assert.That(filter.Matches(ImportRule(connectedSystemId: 3)), Is.False);
    }

    [Test]
    public void Matches_DirectionFilter_OnlyMatchesThatDirection()
    {
        var filter = new SyncRuleFilter { Directions = [SyncRuleDirection.Export] };

        Assert.That(filter.Matches(ExportRule()), Is.True);
        Assert.That(filter.Matches(ImportRule()), Is.False);
    }

    [Test]
    public void Matches_ActionTypeFilter_OnlyMatchesThatActionType()
    {
        var filter = new SyncRuleFilter { ActionTypes = [SyncRuleActionType.Provisions] };

        Assert.That(filter.Matches(ExportRule(provisionToConnectedSystem: true)), Is.True);
        Assert.That(filter.Matches(ImportRule(projectToMetaverse: true)), Is.False);
        Assert.That(filter.Matches(ExportRule(provisionToConnectedSystem: false)), Is.False);
    }

    [Test]
    public void Matches_FlowOnlyActionTypeFilter_MatchesRulesThatNeitherProjectNorProvision()
    {
        var filter = new SyncRuleFilter { ActionTypes = [SyncRuleActionType.FlowOnly] };

        Assert.That(filter.Matches(ImportRule(projectToMetaverse: false)), Is.True);
        Assert.That(filter.Matches(ExportRule(provisionToConnectedSystem: false)), Is.True);
        Assert.That(filter.Matches(ImportRule(projectToMetaverse: true)), Is.False);
    }

    [Test]
    public void Matches_StatusFilter_OnlyMatchesThatStatus()
    {
        var filter = new SyncRuleFilter { Statuses = [SyncRuleStatus.Disabled] };

        Assert.That(filter.Matches(ImportRule(enabled: false)), Is.True);
        Assert.That(filter.Matches(ImportRule(enabled: true)), Is.False);
    }

    [Test]
    public void Matches_BothStatuses_MatchesEnabledAndDisabled()
    {
        var filter = new SyncRuleFilter { Statuses = [SyncRuleStatus.Enabled, SyncRuleStatus.Disabled] };

        Assert.That(filter.Matches(ImportRule(enabled: true)), Is.True);
        Assert.That(filter.Matches(ImportRule(enabled: false)), Is.True);
    }

    #endregion

    #region Free-text search

    [Test]
    public void Matches_Search_MatchesOnNameCaseInsensitively()
    {
        var filter = new SyncRuleFilter { Search = "inbound" };

        Assert.That(filter.Matches(ImportRule(name: "HR Inbound")), Is.True);
        Assert.That(filter.Matches(ExportRule(name: "AD Outbound")), Is.False);
    }

    [Test]
    public void Matches_WhitespaceOnlySearch_MatchesEverything()
    {
        var filter = new SyncRuleFilter { Search = "   " };

        Assert.That(filter.Matches(ImportRule(name: "HR Inbound")), Is.True);
    }

    #endregion

    #region Composition

    [Test]
    public void Matches_FacetsCombineWithAnd()
    {
        var filter = new SyncRuleFilter
        {
            Directions = [SyncRuleDirection.Import],
            Statuses = [SyncRuleStatus.Enabled]
        };

        Assert.That(filter.Matches(ImportRule(enabled: true)), Is.True);
        Assert.That(filter.Matches(ImportRule(enabled: false)), Is.False);
        Assert.That(filter.Matches(ExportRule(enabled: true)), Is.False);
    }

    [Test]
    public void Matches_SearchNarrowsWithinTheFacetResults()
    {
        var filter = new SyncRuleFilter
        {
            Directions = [SyncRuleDirection.Import],
            Search = "HR"
        };

        Assert.That(filter.Matches(ImportRule(name: "HR Inbound")), Is.True);
        Assert.That(filter.Matches(ImportRule(name: "Payroll Inbound")), Is.False);
        // The search term alone must not pull in a rule the facets exclude.
        Assert.That(filter.Matches(ExportRule(name: "HR Outbound")), Is.False);
    }

    [Test]
    public void Matches_RemovingTheSearchLeavesTheFacetResults()
    {
        var facetsOnly = new SyncRuleFilter { Directions = [SyncRuleDirection.Import] };

        Assert.That(facetsOnly.Matches(ImportRule(name: "HR Inbound")), Is.True);
        Assert.That(facetsOnly.Matches(ImportRule(name: "Payroll Inbound")), Is.True);
        Assert.That(facetsOnly.Matches(ExportRule(name: "HR Outbound")), Is.False);
    }

    #endregion
}
