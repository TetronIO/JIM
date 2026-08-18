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
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The G3 adapter (#1115): what flipping a Synchronisation Rule's Outbound Deprovision Action or Inbound
/// Out-of-Scope Action would do to the objects the rule stands over.
///
/// The failures worth testing are the ones that would mislead an administrator into approving a deletion wave:
/// reporting no impact from a toggle that converts every scope exit into a target-system deletion is the worst;
/// counting objects whose fate the edited rule does not actually govern is the next worst, because it is a
/// confident number about a change that does nothing.
/// </summary>
[TestFixture]
public class SyncRuleDestructiveTogglePreviewAdapterTests
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

        _csoDeptAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsoDeptAttributeId,
            Name = "department",
            Type = AttributeDataType.Text
        };
        _csoType = new ConnectedSystemObjectType { Id = CsoTypeId, Name = "User", Attributes = [_csoDeptAttribute] };
        _mvoDeptAttribute = new MetaverseAttribute
        {
            Id = MvoDeptAttributeId,
            Name = "Department",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mvoType = new MetaverseObjectType { Id = MvoTypeId, Name = "Person" };

        _rule = new SyncRule
        {
            Id = RuleId,
            Name = "HR Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
        _rules = [_rule];
        _csos = [];
        _mvos = [];
        _disconnectionCandidates = [];

        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(RuleId)).ReturnsAsync(() => _rule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(SystemId, false, It.IsAny<bool>()))
            .ReturnsAsync(() => _rules);
        _connectedSystemRepo.Setup(r => r.StreamJoinedConnectedSystemObjects(SystemId, CsoTypeId))
            .Returns(() => _csos.ToAsyncEnumerable());
        _connectedSystemRepo.Setup(r => r.GetJoinedConnectedSystemObjectCountAsync(SystemId, CsoTypeId))
            .ReturnsAsync(() => _csos.Count);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectsByIdsNoTrackingAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((IEnumerable<Guid> ids) => _mvos.Where(m => ids.Contains(m.Id)).ToList());
        _metaverseRepo.Setup(r => r.GetMetaverseObjectDisconnectionCandidatesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => _disconnectionCandidates.Where(c => ids.Contains(c.Id)).ToList());

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private SyncRuleDestructiveTogglePreviewAdapter NewAdapter() => new(_jim, new SyncEngine());

    private PreviewContext Context(SyncRuleDestructiveToggleProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.SynchronisationRule,
        ActivityId = Guid.CreateVersion7(),
        TargetId = RuleId,
        ProposedConfiguration = proposal
    };

    private SyncRuleDestructiveToggleProposal UnchangedProposal() =>
        new(_rule.OutboundDeprovisionAction, _rule.InboundOutOfScopeAction);

    /// <summary>
    /// Scopes <paramref name="rule"/> to department == Sales, which is what the fixture's Connected System
    /// Objects and Metaverse Objects carry or do not carry to sit inside or outside scope.
    /// </summary>
    private void GivenRuleScopedToSales(SyncRule rule)
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        if (rule.Direction == SyncRuleDirection.Import)
        {
            group.Criteria.Add(new SyncRuleScopingCriteria
            {
                ConnectedSystemAttribute = _csoDeptAttribute,
                ComparisonType = SearchComparisonType.Equals,
                StringValue = "Sales"
            });
        }
        else
        {
            group.Criteria.Add(new SyncRuleScopingCriteria
            {
                MetaverseAttribute = _mvoDeptAttribute,
                ComparisonType = SearchComparisonType.Equals,
                StringValue = "Sales"
            });
        }
        rule.ObjectScopingCriteriaGroups.Add(group);
    }

    private void GivenExportRule()
    {
        _rule.Direction = SyncRuleDirection.Export;
        _rule.Name = "Directory Export";
    }

    /// <summary>
    /// A joined Connected System Object carrying the given department, joined to a Metaverse Object carrying the
    /// same department (so import scope and export scope agree about which side of Sales it sits on).
    /// </summary>
    private ConnectedSystemObject GivenJoinedCso(string department,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var mvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = _mvoType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            AttributeId = MvoDeptAttributeId,
            Attribute = _mvoDeptAttribute,
            StringValue = department
        });
        _mvos.Add(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemId = SystemId,
            TypeId = CsoTypeId,
            Type = _csoType,
            Status = status,
            MetaverseObjectId = mvo.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            AttributeId = CsoDeptAttributeId,
            StringValue = department
        });
        _csos.Add(cso);
        return cso;
    }

    private void GivenDeletionCandidate(Guid metaverseObjectId) =>
        _disconnectionCandidates.Add(new MetaverseObjectDisconnectionCandidate(
            metaverseObjectId, "Joe Bloggs", MvoTypeId, "Person", MetaverseObjectOrigin.Projected,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            DeletionGracePeriod: null,
            DeletionTriggerConnectedSystemIds: [],
            JoinedConnectedSystemIds: [SystemId]));

    private async Task<List<PreviewDelta>> EvaluateAsync(SyncRuleDestructiveToggleProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in NewAdapter().EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    #region Stage 1: the proposal itself

    [Test]
    public async Task ValidateAsync_NothingChanged_SaysSoWithoutBlockingAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(UnchangedProposal()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information), Is.True,
                "an empty preview with no explanation reads as a broken one");
        }
    }

    [Test]
    public async Task ValidateAsync_OutboundToggleOnImportRule_SaysItHasNoEffectAsync()
    {
        var proposal = UnchangedProposal() with { OutboundDeprovisionAction = OutboundDeprovisionAction.Delete };

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Blocking), Is.False);
            Assert.That(findings.Any(f => f.PropertyName == nameof(SyncRule.OutboundDeprovisionAction)), Is.True,
                "the Outbound Deprovision Action is read only by export rules; silence here leaves an empty preview looking broken");
        }
    }

    [Test]
    public async Task ValidateAsync_DisabledRule_SaysNothingAppliesUntilEnabledAsync()
    {
        _rule.Enabled = false;
        GivenRuleScopedToSales(_rule);
        var proposal = UnchangedProposal() with { InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect };

        var findings = await NewAdapter().ValidateAsync(Context(proposal));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information &&
                                      f.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_AnotherRuleGovernsTheAction_WarnsNamingItAsync()
    {
        // A sibling import rule for the same object type sits ahead of the edited rule, carries its own scoping
        // criteria, and therefore supplies the Out-of-Scope Action on both the scope-exit and obsoletion paths.
        // Changing the edited rule's setting does nothing while that rule exists, and a preview that counted
        // objects for it would be a confident number about a change that does nothing.
        var sibling = new SyncRule
        {
            Id = SiblingRuleId,
            Name = "HR Import (primary)",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
        };
        GivenRuleScopedToSales(sibling);
        GivenRuleScopedToSales(_rule);
        _rules.Insert(0, sibling);
        GivenJoinedCso("Engineering");

        var proposal = UnchangedProposal() with { InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect };

        var findings = await NewAdapter().ValidateAsync(Context(proposal));
        var deltas = await EvaluateAsync(proposal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning &&
                                          f.Message.Contains(sibling.Name)), Is.True,
                "the administrator needs to know which rule actually supplies the action");
            Assert.That(deltas, Is.Empty,
                "no object's fate changes when the edited rule does not govern the action");
        }
    }

    #endregion

    #region Stages 2 to 4: the population

    [Test]
    public async Task EvaluateDeltasAsync_NothingChanged_ReadsNoPopulationAsync()
    {
        GivenJoinedCso("Engineering");

        var deltas = await EvaluateAsync(UnchangedProposal());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Is.Empty);
            _connectedSystemRepo.Verify(r => r.StreamJoinedConnectedSystemObjects(It.IsAny<int>(), It.IsAny<int>()),
                Times.Never, "no setting moved, so no object's fate can have changed and the population must not be read");
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_InboundTightened_OutOfScopeJoinedObjectsDisconnectAndMayDieAsync()
    {
        GivenRuleScopedToSales(_rule);
        var outOfScope = GivenJoinedCso("Engineering");
        GivenJoinedCso("Sales");
        GivenDeletionCandidate(outOfScope.MetaverseObjectId!.Value);

        var proposal = UnchangedProposal() with { InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect };

        var deltas = await EvaluateAsync(proposal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Count(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject), Is.EqualTo(1),
                "only the out-of-scope joined object disconnects; the in-scope one is untouched");
            var disconnect = deltas.Single(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject);
            Assert.That(disconnect.ConnectedSystemObjectId, Is.EqualTo(outOfScope.Id));
            Assert.That(disconnect.ConnectedSystemId, Is.EqualTo(SystemId));
            Assert.That(disconnect.OldValue, Does.Contain("Remain joined"));
            Assert.That(disconnect.NewValue, Does.Contain("Disconnect"));
            Assert.That(deltas.Count(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible), Is.EqualTo(1),
                "the disconnection takes the Metaverse Object's last connector, so its deletion rule fires");
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_InboundRelaxed_ObsoleteAndOutOfScopeObjectsKeepTheirJoinAsync()
    {
        _rule.InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect;
        GivenRuleScopedToSales(_rule);
        var outOfScope = GivenJoinedCso("Engineering");
        var obsolete = GivenJoinedCso("Sales", ConnectedSystemObjectStatus.Obsolete);
        GivenJoinedCso("Sales");

        var proposal = UnchangedProposal() with { InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined };

        var deltas = await EvaluateAsync(proposal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Select(d => d.TransitionType).Distinct(),
                Is.EquivalentTo(new[] { ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined }));
            Assert.That(deltas.Select(d => d.ConnectedSystemObjectId),
                Is.EquivalentTo(new[] { (Guid?)outOfScope.Id, obsolete.Id }),
                "the out-of-scope object and the obsoleted object were both heading for disconnection; the in-scope, " +
                "healthy object never was");
            Assert.That(deltas.All(d => d.OldValue!.Contains("Disconnect") && d.NewValue!.Contains("Remain joined")),
                Is.True);
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_OutboundDisconnectToDelete_ImminentAndExposureTiersAsync()
    {
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        var leaving = GivenJoinedCso("Engineering");
        var managed = GivenJoinedCso("Sales");

        var proposal = UnchangedProposal() with { OutboundDeprovisionAction = OutboundDeprovisionAction.Delete };

        var deltas = await EvaluateAsync(proposal);

        using (Assert.EnterMultipleScope())
        {
            var imminent = deltas.Single(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport);
            Assert.That(imminent.ConnectedSystemObjectId, Is.EqualTo(leaving.Id),
                "the object whose Metaverse Object is already out of scope is deleted at the next synchronisation " +
                "instead of disconnected; that is the count the administrator is consenting to");
            Assert.That(imminent.OldValue, Does.Contain("Disconnect"));
            Assert.That(imminent.NewValue, Does.Contain("Delete"));

            var exposure = deltas.Single(d => d.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction);
            Assert.That(exposure.ConnectedSystemObjectId, Is.EqualTo(managed.Id),
                "every managed object's fate on a future scope exit changes, and the preview states that exposure " +
                "as its own tier rather than mixing it with the imminent deletions");
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_OutboundDeleteToDisconnect_ImminentDeletionBecomesDisconnectionAsync()
    {
        GivenExportRule();
        _rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        GivenRuleScopedToSales(_rule);
        var leaving = GivenJoinedCso("Engineering");

        var proposal = UnchangedProposal() with { OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect };

        var deltas = await EvaluateAsync(proposal);

        var imminent = deltas.Single(d => d.TransitionType ==
            ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(imminent.ConnectedSystemObjectId, Is.EqualTo(leaving.Id));
            Assert.That(imminent.OldValue, Does.Contain("Delete"));
            Assert.That(imminent.NewValue, Does.Contain("Disconnect"));
        }
    }

    [Test]
    public async Task CountImpactAsync_CountsAgreeWithTheDeltasAsync()
    {
        GivenExportRule();
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Engineering");
        GivenJoinedCso("Sales");
        GivenJoinedCso("Sales");

        var proposal = UnchangedProposal() with { OutboundDeprovisionAction = OutboundDeprovisionAction.Delete };

        var counts = await NewAdapter().CountImpactAsync(Context(proposal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Single(c => c.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport).ObjectCount, Is.EqualTo(1));
            Assert.That(counts.Single(c => c.TransitionType ==
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction).ObjectCount, Is.EqualTo(2));
            Assert.That(counts.All(c => c.ConnectedSystemId == SystemId), Is.True,
                "the issue's headline reads per target system, so every count names it");
        }
    }

    [Test]
    public async Task EstimateCostAsync_ReturnsTheJoinedPopulationAsync()
    {
        GivenRuleScopedToSales(_rule);
        GivenJoinedCso("Engineering");
        GivenJoinedCso("Sales");

        var proposal = UnchangedProposal() with { InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect };

        var estimate = await NewAdapter().EstimateCostAsync(Context(proposal));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(2),
            "the walk visits every joined object of the rule's type; the estimate is that population");
    }

    [Test]
    public async Task EstimateCostAsync_NothingChanged_EstimatesZeroAsync()
    {
        GivenJoinedCso("Engineering");

        var estimate = await NewAdapter().EstimateCostAsync(Context(UnchangedProposal()));

        Assert.That(estimate.AffectedObjects, Is.Zero,
            "no setting moved, so the walk reads nothing and the estimate must say so");
    }

    #endregion
}
