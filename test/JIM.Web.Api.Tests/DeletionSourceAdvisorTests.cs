// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests the deletion source advisory (#1256): under When Authoritative Source Disconnected, a Connected System that
/// projects into the Metaverse Object Type but is not a selected authoritative source can create objects no selected
/// source governs. Join-only contributors (projection off) are the normal way to add attribute-only systems and must
/// never be flagged. All three surfaces (portal, REST, PowerShell) derive the finding from this one helper.
/// </summary>
[TestFixture]
public class DeletionSourceAdvisorTests
{
    private const int TypeId = 7;
    private const int HrId = 1;
    private const int ContractorsId = 2;
    private const int PortalId = 3;

    private static SyncRuleHeader Header(int connectedSystemId, string connectedSystemName, bool? projects,
        bool enabled = true, SyncRuleDirection direction = SyncRuleDirection.Import, int typeId = TypeId) => new()
    {
        Id = connectedSystemId * 100,
        Name = $"{connectedSystemName} rule",
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemName = connectedSystemName,
        MetaverseObjectTypeId = typeId,
        Direction = direction,
        Enabled = enabled,
        ProjectToMetaverse = projects
    };

    private static IReadOnlyList<DeletionSourceGap> Gaps(MetaverseObjectDeletionRule rule, int[] sources,
        params SyncRuleHeader[] headers) =>
        DeletionSourceAdvisor.GetUnlistedProjectingSources(rule, sources, TypeId, headers);

    #region GetUnlistedProjectingSources

    [Test]
    public void GetUnlistedProjectingSources_ProjectingSystemNotSelected_ReturnsIt()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(HrId, "HR", projects: true),
            Header(PortalId, "Partner Portal", projects: true));

        Assert.That(gaps, Is.EqualTo(new[] { new DeletionSourceGap(PortalId, "Partner Portal") }));
    }

    [Test]
    public void GetUnlistedProjectingSources_JoinOnlySystemNotSelected_ReturnsNothing()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(HrId, "HR", projects: true),
            Header(ContractorsId, "Contractors DB", projects: false),
            Header(ContractorsId, "Contractors DB", projects: null));

        Assert.That(gaps, Is.Empty, "a join-only contributor is the normal way to add attribute-only systems");
    }

    [Test]
    public void GetUnlistedProjectingSources_ProjectingRuleDisabled_ReturnsNothing()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(PortalId, "Partner Portal", projects: true, enabled: false));

        Assert.That(gaps, Is.Empty, "a disabled rule projects nothing");
    }

    [Test]
    public void GetUnlistedProjectingSources_ProjectingSystemSelected_ReturnsNothing()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId, PortalId],
            Header(HrId, "HR", projects: true),
            Header(PortalId, "Partner Portal", projects: true));

        Assert.That(gaps, Is.Empty);
    }

    [Test]
    public void GetUnlistedProjectingSources_OtherDeletionRules_ReturnNothing()
    {
        var portal = Header(PortalId, "Partner Portal", projects: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Gaps(MetaverseObjectDeletionRule.Manual, [], portal), Is.Empty);
            Assert.That(Gaps(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, [], portal), Is.Empty);
        }
    }

    [Test]
    public void GetUnlistedProjectingSources_RulesForOtherTypesOrExportDirection_AreIgnored()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(PortalId, "Partner Portal", projects: true, typeId: TypeId + 1),
            Header(ContractorsId, "Contractors DB", projects: true, direction: SyncRuleDirection.Export));

        Assert.That(gaps, Is.Empty);
    }

    [Test]
    public void GetUnlistedProjectingSources_SeveralRulesAndSystems_DistinctPerSystemOrderedByName()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(PortalId, "Partner Portal", projects: true),
            Header(PortalId, "Partner Portal", projects: true),
            Header(ContractorsId, "Badge System", projects: true));

        Assert.That(gaps, Is.EqualTo(new[]
        {
            new DeletionSourceGap(ContractorsId, "Badge System"),
            new DeletionSourceGap(PortalId, "Partner Portal")
        }));
    }

    [Test]
    public void GetUnlistedProjectingSources_SystemWithOneJoinOnlyAndOneProjectingRule_ReturnsIt()
    {
        var gaps = Gaps(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, [HrId],
            Header(PortalId, "Partner Portal", projects: false),
            Header(PortalId, "Partner Portal", projects: true));

        Assert.That(gaps.Select(g => g.ConnectedSystemId), Is.EqualTo(new[] { PortalId }));
    }

    #endregion

    #region GetSyncRuleSaveWarning

    private static MetaverseObjectType PersonType(MetaverseObjectDeletionRule rule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
        params int[] sources) => new()
    {
        Id = TypeId,
        Name = "Person",
        DeletionRule = rule,
        DeletionTriggerConnectedSystemIds = sources.Length == 0 ? [HrId] : [.. sources]
    };

    private static SyncRule Rule(bool enabled = true, bool? projects = true, int connectedSystemId = PortalId,
        SyncRuleDirection direction = SyncRuleDirection.Import, int typeId = TypeId) => new()
    {
        Id = 0,
        Name = "Partner Portal: Partner to Person",
        ConnectedSystemId = connectedSystemId,
        MetaverseObjectTypeId = typeId,
        Direction = direction,
        Enabled = enabled,
        ProjectToMetaverse = projects
    };

    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [HrId] = "HR",
        [ContractorsId] = "Contractors DB",
        [PortalId] = "Partner Portal"
    };

    private static SyncRuleDeletionSourceWarning? SaveWarning(SyncRule proposed, int? projectedTypeIdBefore,
        MetaverseObjectType? type = null) =>
        DeletionSourceAdvisor.GetSyncRuleSaveWarning(proposed, projectedTypeIdBefore, type ?? PersonType(), Names);

    [Test]
    public void GetProjectedMetaverseObjectTypeId_EnabledProjectingImportRule_ReturnsType()
    {
        Assert.That(DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(Rule()), Is.EqualTo(TypeId));
    }

    [Test]
    public void GetProjectedMetaverseObjectTypeId_RuleThatProjectsNothing_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(Rule(enabled: false)), Is.Null);
            Assert.That(DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(Rule(projects: false)), Is.Null);
            Assert.That(DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(Rule(projects: null)), Is.Null);
            Assert.That(DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(Rule(direction: SyncRuleDirection.Export)), Is.Null);
        }
    }

    [Test]
    public void GetSyncRuleSaveWarning_NewProjectingRuleFromUnlistedSystem_Warns()
    {
        var warning = SaveWarning(Rule(), projectedTypeIdBefore: null);

        Assert.That(warning, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning!.ConnectedSystemName, Is.EqualTo("Partner Portal"));
            Assert.That(warning.ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(warning.AuthoritativeSourceNames, Is.EqualTo(new[] { "HR" }));
            Assert.That(warning.Message, Does.Contain("Partner Portal").And.Contain("HR"),
                "the REST and PowerShell message must name both the projecting system and the selected sources");
        }
    }

    [Test]
    public void GetSyncRuleSaveWarning_ProjectingRuleAlreadyProjectingIntoType_DoesNotWarnAgain()
    {
        Assert.That(SaveWarning(Rule(), projectedTypeIdBefore: TypeId), Is.Null,
            "re-saving a rule that already projected must not nag on every save");
    }

    [Test]
    public void GetSyncRuleSaveWarning_ProjectionSwitchedOnOrRuleEnabledOrRetargeted_Warns()
    {
        // Each case reaches "enabled and projecting into this type" from somewhere that was not.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SaveWarning(Rule(), projectedTypeIdBefore: null), Is.Not.Null, "projection switched on, or rule enabled");
            Assert.That(SaveWarning(Rule(), projectedTypeIdBefore: TypeId + 1), Is.Not.Null, "rule retargeted from another type");
        }
    }

    [Test]
    public void GetSyncRuleSaveWarning_RuleThatDoesNotProject_DoesNotWarn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SaveWarning(Rule(projects: false), projectedTypeIdBefore: null), Is.Null, "join-only");
            Assert.That(SaveWarning(Rule(enabled: false), projectedTypeIdBefore: null), Is.Null, "disabled");
        }
    }

    [Test]
    public void GetSyncRuleSaveWarning_SystemIsAnAuthoritativeSource_DoesNotWarn()
    {
        Assert.That(SaveWarning(Rule(), projectedTypeIdBefore: null, PersonType(sources: [HrId, PortalId])), Is.Null);
    }

    [Test]
    public void GetSyncRuleSaveWarning_TypeNotUsingAuthoritativeSourceRule_DoesNotWarn()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SaveWarning(Rule(), null, PersonType(MetaverseObjectDeletionRule.Manual)), Is.Null);
            Assert.That(SaveWarning(Rule(), null, PersonType(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)), Is.Null);
        }
    }

    [Test]
    public void GetSyncRuleSaveWarning_RuleProjectsIntoADifferentType_DoesNotWarn()
    {
        Assert.That(SaveWarning(Rule(typeId: TypeId + 1), projectedTypeIdBefore: null), Is.Null,
            "the type passed in is not the one the rule projects into, so its deletion settings do not apply");
    }

    [Test]
    public void GetSyncRuleSaveWarning_SeveralAuthoritativeSources_NamesEach()
    {
        var warning = SaveWarning(Rule(), null, PersonType(sources: [HrId, ContractorsId]));

        Assert.That(warning!.AuthoritativeSourceNames, Is.EqualTo(new[] { "Contractors DB", "HR" }));
    }

    #endregion
}
