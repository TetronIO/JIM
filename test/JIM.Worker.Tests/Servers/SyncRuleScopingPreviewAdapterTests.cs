// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The G1 adapter (#1436): what changing a Synchronisation Rule's Scoping Criteria would do to the objects it
/// manages.
///
/// The failures worth testing are the ones that would mislead an administrator into approving a disconnection
/// wave. Reporting no impact from a narrowing that disconnects thousands of joined objects is the worst. Counting
/// a departure that would not actually happen is the next worst: an import rule's scope exit only bites when the
/// object leaves the scope of EVERY import rule carrying criteria, so a narrowing beside a criteria-less sibling
/// rule takes nothing out of scope at all, and a confident count there is a lie about a change that does nothing.
/// </summary>
[TestFixture]
public class SyncRuleScopingPreviewAdapterTests
{
    private const int RuleId = 42;
    private const int SiblingRuleId = 17;
    private const int SystemId = 5;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int CsoDeptAttributeId = 101;
    private const int MvoDeptAttributeId = 201;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;

    private SyncRule _rule = null!;
    private List<SyncRule> _rules = null!;
    private List<ConnectedSystemObject> _csos = null!;
    private List<MetaverseObject> _mvos = null!;
    private List<MetaverseObjectDisconnectionCandidate> _disconnectionCandidates = null!;

    private ConnectedSystemObjectType _csoType = null!;
    private ConnectedSystemObjectTypeAttribute _csoDeptAttribute = null!;
    private MetaverseObjectType _mvoType = null!;
    private MetaverseAttribute _mvoDeptAttribute = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _csoDeptAttribute = new ConnectedSystemObjectTypeAttribute { Id = CsoDeptAttributeId, Name = "department", Type = AttributeDataType.Text };
        _csoType = new ConnectedSystemObjectType { Id = CsoTypeId, Name = "User", Attributes = [_csoDeptAttribute] };
        _mvoDeptAttribute = new MetaverseAttribute
        {
            Id = MvoDeptAttributeId,
            Name = "Department",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mvoType = new MetaverseObjectType { Id = MvoTypeId, Name = "Person", Attributes = [_mvoDeptAttribute] };

        _rule = new SyncRule
        {
            Id = RuleId,
            Name = "HR Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            ConnectedSystemObjectType = _csoType,
            MetaverseObjectTypeId = MvoTypeId,
            MetaverseObjectType = _mvoType,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
        };
        _rules = [_rule];
        _csos = [];
        _mvos = [];
        _disconnectionCandidates = [];

        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(RuleId)).ReturnsAsync(() => _rule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(SystemId, false, It.IsAny<bool>())).ReturnsAsync(() => _rules);
        _connectedSystemRepo.Setup(r => r.StreamConnectedSystemObjectsOfType(SystemId, CsoTypeId)).Returns(() => _csos.ToAsyncEnumerable());
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(SystemId, CsoTypeId)).ReturnsAsync(() => _csos.Count);
        _connectedSystemRepo.Setup(r => r.GetObjectTypeAsync(CsoTypeId)).ReturnsAsync(() => _csoType);
        _metaverseRepo.Setup(r => r.StreamMetaverseObjectsOfType(MvoTypeId)).Returns(() => _mvos.ToAsyncEnumerable());
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(MvoTypeId, It.IsAny<bool>())).ReturnsAsync(() => _mvoType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(MvoTypeId)).ReturnsAsync(() => _mvos.Count);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectDisconnectionCandidatesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => _disconnectionCandidates.Where(c => ids.Contains(c.Id)).ToList());

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // ── Validation ───────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ValidateAsync_ProposalMatchesStoredScope_ReportsNothingChangesAsync()
    {
        GivenRuleScopedToSales(_rule);

        var findings = await NewAdapter().ValidateAsync(Context(SyncRuleScopingProposal.FromCurrentScope(_rule)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].Severity, Is.EqualTo(PreviewValidationSeverity.Information));
            Assert.That(findings[0].Message, Does.Contain("no object").IgnoreCase);
        }
    }

    [Test]
    public async Task ValidateAsync_ScopeRemovedEntirely_WarnsTheRuleWouldCoverEverythingAsync()
    {
        // Deleting the last criterion is one click and reads as tidying up, but it hands the rule every object of
        // its type: the widest change the Scope tab can make, and the one least likely to look like a change.
        GivenRuleScopedToSales(_rule);

        var findings = await NewAdapter().ValidateAsync(Context(new SyncRuleScopingProposal([])));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("every", StringComparison.OrdinalIgnoreCase)), Is.True,
            "removing all Scoping Criteria must be called out as covering every object of the type");
    }

    [Test]
    public async Task ValidateAsync_ImportRuleBesideACriteriaLessSibling_WarnsNothingCanLeaveScopeAsync()
    {
        // The engine's scope-exit path only fires when an object is out of scope of every import rule carrying
        // criteria. A criteria-less sibling is in scope for everything, so narrowing this rule disconnects nobody,
        // and a preview that counted departures here would be confidently wrong.
        GivenRuleScopedToSales(_rule);
        _rules.Add(new SyncRule
        {
            Id = SiblingRuleId,
            Name = "Catch-all Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        });

        var findings = await NewAdapter().ValidateAsync(Context(ProposalScopedTo("Marketing")));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("Catch-all Import", StringComparison.Ordinal)), Is.True,
            "the warning must name the sibling rule that keeps every object in scope");
    }

    [Test]
    public async Task ValidateAsync_DisabledRule_SaysTheCountsDescribeALaterRunAsync()
    {
        GivenRuleScopedToSales(_rule);
        _rule.Enabled = false;

        var findings = await NewAdapter().ValidateAsync(Context(ProposalScopedTo("Marketing")));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information
            && f.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_CriterionNamingAnAttributeOfTheWrongSide_IsAnErrorAsync()
    {
        // An import rule reads Connected System attributes; a Metaverse Attribute on one can never be evaluated,
        // so the criterion would silently contribute nothing rather than narrowing anything.
        GivenRuleScopedToSales(_rule);
        var proposal = new SyncRuleScopingProposal([
            new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All,
                [new SyncRuleScopingCriterionProposal(MvoDeptAttributeId, null, SearchComparisonType.Equals, StringValue: "Sales")], [])
        ]);

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.True);
    }

    // ── Import: leaving scope ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EvaluateDeltasAsync_NarrowingDisconnectsAJoinedObject_ReportsTheDisconnectionAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject));
    }

    [Test]
    public async Task EvaluateDeltasAsync_NarrowingWithRemainJoined_ReportsTheScopeExitWithoutADisconnectionAsync()
    {
        // The object stops receiving Attribute Flow but keeps its join, which is a real consequence and a
        // materially different one: nothing is recalled from the Metaverse and no identity becomes deletable.
        GivenRuleScopedToSales(_rule);
        _rule.InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined;
        GivenJoinedCso("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_NarrowingAnUnjoinedObjectOutOfScope_ReportsAScopeExitOnlyAsync()
    {
        // An unjoined object leaving scope costs JIM nothing; it was never going to be anything but a projection
        // candidate. Counting it beside the disconnections would inflate the destructive headline.
        GivenRuleScopedToSales(_rule);
        GivenUnjoinedCso("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObjectInScopeUnderBothScopes_YieldsNoDeltaAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");

        // Widening to "Sales or Marketing" leaves the Sales object exactly where it was.
        var deltas = await EvaluateAsync(ProposalScopedToAnyOf("Sales", "Marketing"));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_CriteriaLessSiblingKeepsEverythingInScope_ReportsNoDisconnectionsAsync()
    {
        // The counterpart to the validation warning: the honesty has to hold in the numbers too, not just in the
        // findings, or the count states a disconnection wave the next synchronisation would never perform.
        GivenRuleScopedToSales(_rule);
        _rules.Add(new SyncRule
        {
            Id = SiblingRuleId,
            Name = "Catch-all Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        });
        GivenJoinedCso("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject));
    }

    // ── Downstream ───────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EvaluateDeltasAsync_DisconnectionLeavesItsIdentityWithNoConnectors_ReportsDeletionEligibilityAsync()
    {
        GivenRuleScopedToSales(_rule);
        var cso = GivenJoinedCso("Sales");
        GivenIdentityWouldBecomeDeletionEligible(cso.MetaverseObjectId!.Value);

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
    }

    // ── Counts ───────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CountImpactAsync_TwoObjectsLeavingScope_CountsBothUnderTheirTransitionAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");
        GivenJoinedCso("Sales");

        var counts = await NewAdapter().CountImpactAsync(Context(ProposalScopedTo("Marketing")));

        var disconnections = counts.SingleOrDefault(c =>
            c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject);
        Assert.That(disconnections, Is.Not.Null);
        Assert.That(disconnections!.ObjectCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CountImpactAsync_ProposalMatchesStoredScope_CountsNothingAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");

        var counts = await NewAdapter().CountImpactAsync(Context(SyncRuleScopingProposal.FromCurrentScope(_rule)));

        Assert.That(counts, Is.Empty);
    }

    [Test]
    public async Task EstimateCostAsync_CountsThePopulationTheWalkWouldRead()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");
        GivenUnjoinedCso("Engineering");

        var estimate = await NewAdapter().EstimateCostAsync(Context(ProposalScopedTo("Marketing")));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(2),
            "the walk reads every object of the type, joined or not, because a widening projects the unjoined ones");
    }

    [Test]
    public async Task EstimateCostAsync_ProposalMatchesStoredScope_IsZeroAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Sales");

        var estimate = await NewAdapter().EstimateCostAsync(Context(SyncRuleScopingProposal.FromCurrentScope(_rule)));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(0));
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EvaluateDeltasAsync_ExportNarrowingWithDeleteAction_ReportsADeleteExportAsync()
    {
        // The destructive headline for an export rule: objects leaving scope are removed from the target system.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        GivenMvoWithTargetObject("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport));
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportNarrowingWithDisconnectAction_ReportsADisconnectionAsync()
    {
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        GivenMvoWithTargetObject("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject));
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportWideningOntoAnIdentityWithNoTargetObject_ReportsProvisioningAsync()
    {
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = true;
        GivenMvoWithoutTargetObject("Marketing");

        var deltas = await EvaluateAsync(ProposalScopedToAnyOf("Sales", "Marketing"));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportWideningWithoutProvisioning_ReportsScopeEntryOnlyAsync()
    {
        // A rule that does not provision brings the identity into scope for Attribute Flow and creates nothing,
        // so reporting a provisioning would overstate what the change does.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = false;
        GivenMvoWithoutTargetObject("Marketing");

        var deltas = await EvaluateAsync(ProposalScopedToAnyOf("Sales", "Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportWideningOntoAnIdentityWithATargetObject_ReportsScopeEntryOnlyAsync()
    {
        // The target object already exists, so even a provisioning rule creates nothing; it begins flowing
        // attributes to the object that is there.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = true;
        GivenMvoWithTargetObject("Marketing");

        var deltas = await EvaluateAsync(ProposalScopedToAnyOf("Sales", "Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportNarrowingAwayFromAnIdentityWithNoTargetObject_ReportsAScopeExitWithNothingToRemoveAsync()
    {
        // The rule never created anything for this identity, so leaving its scope removes nothing from the target
        // system. The object is a Metaverse Object leaving an EXPORT rule, so the import-side scope exit, which
        // the panel labels "Leaves import scope", would name a direction this rule does not have.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = false;
        GivenMvoWithoutTargetObject("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportNarrowingAwayFromAnUnprovisionedIdentityWhenProvisioning_ReportsItWouldStopProvisioningAsync()
    {
        // The mirror of the widening case above: a rule that provisions would have created a Connected System Object
        // for this identity, and under the proposal it would not. The consequence is the object that never arrives,
        // not the scope exit itself.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = true;
        GivenMvoWithoutTargetObject("Sales");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportWalk_NeverReportsTheImportScopeTransitionsAsync()
    {
        // An export rule's population is Metaverse Objects, and none of them can leave or enter import scope. The
        // import-side transitions are the ones the panel labels "import scope", so their appearance on an export
        // preview is exactly the mislabel this guards against.
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        _rule.ProvisionToConnectedSystem = false;
        GivenMvoWithoutTargetObject("Sales");
        GivenMvoWithoutTargetObject("Marketing");

        var deltas = await EvaluateAsync(ProposalScopedTo("Marketing"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(2), "one identity leaves scope and one enters it");
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope));
        }
    }

    // ── Contract ─────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Adapter_DeclaresTheScopeSurfaceAndItsProposalType()
    {
        var adapter = NewAdapter();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.SynchronisationRuleScope));
            Assert.That(adapter.ProposalType, Is.EqualTo(typeof(SyncRuleScopingProposal)));
            Assert.That(adapter.ProducesDeltas, Is.True);
        }
    }

    #region helpers

    private SyncRuleScopingPreviewAdapter NewAdapter() => new(_jim, new SyncEngine());

    private PreviewContext Context(SyncRuleScopingProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.SynchronisationRuleScope,
        ActivityId = Guid.CreateVersion7(),
        TargetId = RuleId,
        ProposedConfiguration = proposal
    };

    private async Task<List<PreviewDelta>> EvaluateAsync(SyncRuleScopingProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in NewAdapter().EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private SyncRuleScopingProposal ProposalScopedTo(string department) =>
        new([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.All, [CriterionFor(department)], [])]);

    private SyncRuleScopingProposal ProposalScopedToAnyOf(params string[] departments) =>
        new([new SyncRuleScopingCriteriaGroupProposal(SearchGroupType.Any, [.. departments.Select(CriterionFor)], [])]);

    private SyncRuleScopingCriterionProposal CriterionFor(string department) => _rule.Direction == SyncRuleDirection.Import
        ? new SyncRuleScopingCriterionProposal(null, CsoDeptAttributeId, SearchComparisonType.Equals, StringValue: department)
        : new SyncRuleScopingCriterionProposal(MvoDeptAttributeId, null, SearchComparisonType.Equals, StringValue: department);

    private void GivenRuleScopedToSales(SyncRule rule)
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(rule.Direction == SyncRuleDirection.Import
            ? new SyncRuleScopingCriteria { ConnectedSystemAttribute = _csoDeptAttribute, ConnectedSystemAttributeId = CsoDeptAttributeId, ComparisonType = SearchComparisonType.Equals, StringValue = "Sales" }
            : new SyncRuleScopingCriteria { MetaverseAttribute = _mvoDeptAttribute, MetaverseAttributeId = MvoDeptAttributeId, ComparisonType = SearchComparisonType.Equals, StringValue = "Sales" });
        rule.ObjectScopingCriteriaGroups.Add(group);
    }

    private void GivenExportRule()
    {
        _rule.Direction = SyncRuleDirection.Export;
        _rule.Name = "Directory Export";
    }

    private ConnectedSystemObject GivenJoinedCso(string department)
    {
        var mvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = _mvoType };
        _mvos.Add(mvo);
        var cso = NewCso(department);
        cso.MetaverseObjectId = mvo.Id;
        cso.MetaverseObject = mvo;
        return cso;
    }

    private ConnectedSystemObject GivenUnjoinedCso(string department) => NewCso(department);

    private ConnectedSystemObject NewCso(string department)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemId = SystemId,
            TypeId = CsoTypeId,
            Type = _csoType,
            Status = ConnectedSystemObjectStatus.Normal
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = CsoDeptAttributeId,
            Attribute = _csoDeptAttribute,
            StringValue = department
        });
        _csos.Add(cso);
        return cso;
    }

    private MetaverseObject GivenMvoWithTargetObject(string department)
    {
        var mvo = NewMvo(department);
        mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemId = SystemId,
            TypeId = CsoTypeId,
            Type = _csoType,
            MetaverseObjectId = mvo.Id
        });
        return mvo;
    }

    private MetaverseObject GivenMvoWithoutTargetObject(string department) => NewMvo(department);

    private MetaverseObject NewMvo(string department)
    {
        var mvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = _mvoType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = MvoDeptAttributeId,
            Attribute = _mvoDeptAttribute,
            StringValue = department
        });
        _mvos.Add(mvo);
        return mvo;
    }

    private void GivenIdentityWouldBecomeDeletionEligible(Guid metaverseObjectId) =>
        _disconnectionCandidates.Add(new MetaverseObjectDisconnectionCandidate(
            metaverseObjectId,
            "Alice Example",
            MvoTypeId,
            "Person",
            MetaverseObjectOrigin.Projected,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            DeletionGracePeriod: null,
            DeletionTriggerConnectedSystemIds: [],
            JoinedConnectedSystemIds: [SystemId]));

    #endregion
}
