// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The behaviour-toggle adapter (#1462): what turning a Synchronisation Rule on, off, round, or loose would do.
///
/// These five settings are the ones whose consequences are hardest to picture, because none of them names a
/// population. Disabling a rule reads like pausing it and is closer to withdrawing every value it owns; turning
/// Provision To Connected System on reads like a capability and is account creation at scale. So the preview's job
/// is to attach a count to each, and its job on Direction is to refuse: flipping it leaves every mapping and every
/// Object Matching Rule pointing at the side the rule is leaving, so there is no coherent proposal to evaluate.
/// </summary>
[TestFixture]
public class SyncRuleBehaviourTogglePreviewAdapterTests
{
    private const int RuleId = 42;
    private const int SystemId = 5;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;
    private SyncRule _rule = null!;
    private MetaverseObjectType _mvoType = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _mvoType = new MetaverseObjectType { Id = MvoTypeId, Name = "Person", Attributes = [] };
        _rule = new SyncRule
        {
            Id = RuleId,
            Name = "HR Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = CsoTypeId, Name = "User", Attributes = [] },
            MetaverseObjectTypeId = MvoTypeId,
            MetaverseObjectType = _mvoType,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ProjectToMetaverse = true,
            EnforceState = true
        };

        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(RuleId)).ReturnsAsync(() => _rule);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(SystemId, CsoTypeId)).ReturnsAsync(0);
        _connectedSystemRepo.Setup(r => r.GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, CsoTypeId)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(MvoTypeId, It.IsAny<bool>())).ReturnsAsync(() => _mvoType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(MvoTypeId)).ReturnsAsync(0);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private SyncRuleBehaviourTogglePreviewAdapter NewAdapter() => new(_jim, new SyncEngine());

    private PreviewContext Context(SyncRuleBehaviourToggleProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour,
        ActivityId = Guid.NewGuid(),
        TargetId = RuleId,
        ProposedConfiguration = proposal
    };

    private SyncRuleBehaviourToggleProposal Stored() => SyncRuleBehaviourToggleProposal.FromCurrentSettings(_rule);

    [Test]
    public void Surface_IsTheBehaviourSurface()
    {
        var adapter = NewAdapter();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.SynchronisationRuleBehaviour));
            Assert.That(adapter.ProducesDeltas, Is.True);
            Assert.That(adapter.ProposalType, Is.EqualTo(typeof(SyncRuleBehaviourToggleProposal)));
        }
    }

    [Test]
    public async Task ValidateAsync_ProposalMatchesStoredSettings_ReportsNoChangeAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(Stored()));

        Assert.That(findings.Select(f => f.Message), Has.Some.Contains("already"));
    }

    [Test]
    public async Task ValidateAsync_DirectionFlipped_IsBlockingAsync()
    {
        // Not an evaluation but a refusal: an import rule's mappings write Metaverse Attributes and its Object
        // Matching Rules search the Metaverse. Flipped to Export, every one of them addresses the wrong side, so
        // there is nothing coherent to put to the engine.
        var findings = await NewAdapter().ValidateAsync(
            Context(Stored() with { Direction = SyncRuleDirection.Export }));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).Select(f => f.Message),
            Has.Some.Contains("Direction"));
    }

    [Test]
    public async Task ValidateAsync_RuleBeingDisabled_WarnsWhatItStopsDoingAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(Stored() with { Enabled = false }));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Warning).Select(f => f.Message),
            Has.Some.Contains("contribut").IgnoreCase);
    }

    [Test]
    public async Task ValidateAsync_ProvisioningTurnedOn_WarnsThatAccountsWouldBeCreatedAsync()
    {
        _rule.Direction = SyncRuleDirection.Export;
        _rule.ProvisionToConnectedSystem = false;

        var findings = await NewAdapter().ValidateAsync(
            Context(Stored() with { ProvisionToConnectedSystem = true }));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Warning).Select(f => f.Message),
            Has.Some.Contains("account").IgnoreCase);
    }

    [Test]
    public async Task ValidateAsync_EnforceStateTurnedOff_WarnsObjectsAreFreeToDriftAsync()
    {
        _rule.Direction = SyncRuleDirection.Export;

        var findings = await NewAdapter().ValidateAsync(Context(Stored() with { EnforceState = false }));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Warning).Select(f => f.Message),
            Has.Some.Contains("drift").IgnoreCase);
    }

    [Test]
    public async Task ValidateAsync_ExportOnlyToggleOnAnImportRule_IsReportedAsNotApplicableAsync()
    {
        // Enforce State governs export drift remediation only, so changing it on an import rule does nothing at
        // all. Said plainly rather than counted as zero, which would read as "nothing is affected yet".
        var findings = await NewAdapter().ValidateAsync(Context(Stored() with { EnforceState = false }));

        Assert.That(findings.Select(f => f.Message), Has.Some.Contains("Export"));
    }

    [Test]
    public async Task EstimateCostAsync_NoChange_CostsNothingAsync()
    {
        var estimate = await NewAdapter().EstimateCostAsync(Context(Stored()));

        Assert.That(estimate.AffectedObjects, Is.Zero);
    }

    [Test]
    public async Task EvaluateDeltasAsync_NoChange_ReadsNoObjectsAsync()
    {
        var deltas = await NewAdapter().EvaluateDeltasAsync(Context(Stored()), CancellationToken.None).ToListAsync();

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_DirectionFlipped_EvaluatesNothingAsync()
    {
        // The refusal has to bite here too: a blocking finding that still streamed deltas would put counts on a
        // screen beside a message saying the change cannot be applied.
        var deltas = await NewAdapter()
            .EvaluateDeltasAsync(Context(Stored() with { Direction = SyncRuleDirection.Export }), CancellationToken.None)
            .ToListAsync();

        Assert.That(deltas, Is.Empty);
    }
}
