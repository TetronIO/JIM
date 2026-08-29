// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The G2 adapter (#1437): what changing a Synchronisation Rule's Attribute Flow would write.
///
/// The evaluation is the #288 engine's own, run twice per object (stored configuration, then the proposal) and
/// diffed, so the adapter never forms an opinion about what a mapping produces. That is what these tests hold it
/// to: the fixture seeds a real sync repository and the assertions are about the VALUES the engine reports, not
/// about the adapter's reading of the configuration.
///
/// The failures worth testing are the ones that would let a bad cutover through. Reporting no change where a
/// mapping rewrites every address is the worst. Next worst is a confident old-to-new pair for a mapping that
/// would never win its attribute: Attribute Priority decides whether a proposed mapping writes anything at all,
/// and a preview that ignores it states values that would never be written.
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowPreviewAdapterTests
{
    private const int RuleId = 42;
    private const int OtherSystemRuleId = 77;
    private const int SystemId = 5;
    private const int OtherSystemId = 6;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int CsFirstNameAttributeId = 101;
    private const int CsLastNameAttributeId = 102;
    private const int CsEmailAttributeId = 103;
    private const int MvEmailAttributeId = 201;
    private const int MvAlternateEmailAttributeId = 202;
    private const int MappingId = 900;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private SyncRepository _syncRepo = null!;
    private JimApplication _jim = null!;

    private SyncRule _rule = null!;
    private List<SyncRule> _rules = null!;
    private List<ConnectedSystemObject> _csos = null!;
    private List<MetaverseObject> _mvos = null!;

    private ConnectedSystemObjectType _csoType = null!;
    private ConnectedSystemObjectTypeAttribute _csFirstName = null!;
    private ConnectedSystemObjectTypeAttribute _csLastName = null!;
    private ConnectedSystemObjectTypeAttribute _csEmail = null!;
    private MetaverseObjectType _mvoType = null!;
    private MetaverseAttribute _mvEmail = null!;
    private MetaverseAttribute _mvAlternateEmail = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _csFirstName = new ConnectedSystemObjectTypeAttribute { Id = CsFirstNameAttributeId, Name = "givenName", Type = AttributeDataType.Text };
        _csLastName = new ConnectedSystemObjectTypeAttribute { Id = CsLastNameAttributeId, Name = "sn", Type = AttributeDataType.Text };
        _csEmail = new ConnectedSystemObjectTypeAttribute { Id = CsEmailAttributeId, Name = "mail", Type = AttributeDataType.Text };
        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "User",
            ConnectedSystemId = SystemId,
            Attributes = [_csFirstName, _csLastName, _csEmail]
        };

        _mvEmail = new MetaverseAttribute
        {
            Id = MvEmailAttributeId,
            Name = "Email",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mvAlternateEmail = new MetaverseAttribute
        {
            Id = MvAlternateEmailAttributeId,
            Name = "Alternate Email",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mvoType = new MetaverseObjectType { Id = MvoTypeId, Name = "Person", Attributes = [_mvEmail, _mvAlternateEmail] };

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
            ProjectToMetaverse = true
        };
        _rule.AttributeFlowRules.Add(DirectMapping(_rule, MappingId, _mvEmail, _csEmail));

        _rules = [_rule];
        _csos = [];
        _mvos = [];

        _syncRepo = new SyncRepository();
        _syncRepo.SeedConnectedSystem(new ConnectedSystem { Id = SystemId, Name = "HR" });
        _syncRepo.SeedObjectType(_csoType);
        _syncRepo.SeedSyncRule(_rule);

        _connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(RuleId)).ReturnsAsync(() => _rule);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(SystemId, false, It.IsAny<bool>())).ReturnsAsync(() => _rules);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync()).ReturnsAsync(() => _rules);
        _connectedSystemRepo.Setup(r => r.StreamConnectedSystemObjectsOfType(SystemId, CsoTypeId)).Returns(() => _csos.ToAsyncEnumerable());
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(SystemId, CsoTypeId)).ReturnsAsync(() => _csos.Count);
        _connectedSystemRepo.Setup(r => r.GetObjectTypeAsync(CsoTypeId)).ReturnsAsync(() => _csoType);
        _metaverseRepo.Setup(r => r.StreamMetaverseObjectsOfType(MvoTypeId)).Returns(() => _mvos.ToAsyncEnumerable());
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(MvoTypeId, It.IsAny<bool>())).ReturnsAsync(() => _mvoType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(MvoTypeId)).ReturnsAsync(() => _mvos.Count);

        _jim = new JimApplication(_repo.Object, syncRepository: _syncRepo);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // ── Validation ───────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ValidateAsync_ProposalMatchesStoredMappings_ReportsNothingChangesAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(SyncRuleAttributeFlowProposal.FromCurrentMappings(_rule)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].Severity, Is.EqualTo(PreviewValidationSeverity.Information));
            Assert.That(findings[0].Message, Does.Contain("no object").IgnoreCase);
        }
    }

    [Test]
    public async Task ValidateAsync_ProposalRemovesEveryMapping_WarnsTheRuleWouldFlowNothingAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(new SyncRuleAttributeFlowProposal([])));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("flow nothing", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_RuleDisabled_SaysNoSynchronisationAppliesItTodayAsync()
    {
        _rule.Enabled = false;

        var findings = await NewAdapter().ValidateAsync(Context(ProposalWritingEmailFrom(CsFirstNameAttributeId)));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information
            && f.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_AttributeIsAlsoWrittenFromAnotherConnectedSystem_SaysThePreviewCoversThisSystemOnlyAsync()
    {
        // The honesty that matters most here. A surviving contributor on another Connected System is what the
        // recall re-elects to, but what it would write is evaluated by that system's synchronisation, which
        // this preview does not run. Silence would let an administrator read the withdrawal as a permanent loss.
        GivenAnImportRuleOnAnotherSystemWritingEmail();

        var findings = await NewAdapter().ValidateAsync(Context(new SyncRuleAttributeFlowProposal([])));

        Assert.That(findings.Any(f => f.Message.Contains("Directory Import", StringComparison.Ordinal)
            && f.Message.Contains("re-elects", StringComparison.OrdinalIgnoreCase)
            && f.Message.Contains("not counted below", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_ProposedMappingCannotWinItsAttribute_WarnsItWouldWriteNothingAsync()
    {
        // Attribute Priority decides whether a mapping writes at all. A proposal an administrator has just
        // carefully composed, sitting behind a higher-priority contributor, writes nothing; reporting the values
        // it would produce without saying so is a confident statement about a write that never happens.
        GivenAnImportRuleOnAnotherSystemWritingEmail(priority: 1);

        var findings = await NewAdapter().ValidateAsync(Context(ProposalWritingEmailFrom(CsFirstNameAttributeId, priority: 5)));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("Attribute Priority", StringComparison.Ordinal)), Is.True);
    }

    // ── Value deltas ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EvaluateDeltasAsync_ProposalMatchesStoredMappings_YieldsNothingAsync()
    {
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local");

        var deltas = await EvaluateAsync(SyncRuleAttributeFlowProposal.FromCurrentMappings(_rule));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposalRetargetsTheSource_ReportsTheOldAndNewValueAsync()
    {
        // The headline: the address every managed identity would be given, stated before the flow runs.
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local", firstName: "ada.lovelace@corp.local");

        var deltas = await EvaluateAsync(ProposalWritingEmailFrom(CsFirstNameAttributeId));

        var delta = deltas.SingleOrDefault(d => d.AttributeName == "Email");
        Assert.That(delta, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta!.OldValue, Is.EqualTo("ada@corp.local"));
            Assert.That(delta!.NewValue, Is.EqualTo("ada.lovelace@corp.local"));
            Assert.That(delta!.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposedMappingProducesNoValue_ReportsTheWithdrawalAsync()
    {
        // A mapping that still exists but has nothing to give retracts what it wrote, and the engine reports that
        // as a removal. This is the withdrawal an administrator has to see before saving: the identity is left
        // with no address at all.
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local");

        var deltas = await EvaluateAsync(ProposalWritingEmailFrom(CsFirstNameAttributeId));

        var delta = deltas.SingleOrDefault(d => d.AttributeName == "Email");
        Assert.That(delta, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta!.OldValue, Is.EqualTo("ada@corp.local"));
            Assert.That(delta!.NewValue, Is.Null);
            Assert.That(delta!.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_MappingRemovedEntirely_CountsNoValueChangeBecauseTheRecallIsNotThisSaveAsync()
    {
        // The deltas describe what SAVING does. A removed mapping's contributed values are withdrawn by the
        // orphan recall (#1533/#1536), but that runs at the next Full Synchronisation of the contributing
        // system, not in the save, so counting the withdrawals here would claim the save does something it
        // does not. The recall (or a keep choice) is said in a finding instead.
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local");

        var deltas = await EvaluateAsync(new SyncRuleAttributeFlowProposal([]));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task ValidateAsync_MappingRemovedEntirely_SaysTheValuesAreRecalledAtTheNextFullSynchronisationAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(new SyncRuleAttributeFlowProposal([])));

        var finding = findings.SingleOrDefault(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("Email", StringComparison.Ordinal));
        Assert.That(finding, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding!.Message, Does.Contain("recalled at the next Full Synchronisation"));
            Assert.That(finding!.Message, Does.Not.Contain("left in place"));
            Assert.That(finding!.MetaverseAttributeName, Is.EqualTo("Email"),
                "the finding names its attribute so the portal can render the standard Metaverse attribute chip");
        }
    }

    [Test]
    public async Task ValidateAsync_MappingRemovedWithKeepChosen_SaysTheValuesAreKeptAsync()
    {
        // The deletion-time choice (#1537) travels on the proposal so the preview describes what will really
        // happen: keep severs provenance at save, and nothing ever recalls the values.
        var findings = await NewAdapter().ValidateAsync(Context(
            new SyncRuleAttributeFlowProposal([], KeepContributedValuesAttributeIds: [MvEmailAttributeId])));

        var finding = findings.SingleOrDefault(f => f.Severity == PreviewValidationSeverity.Warning
            && f.Message.Contains("Email", StringComparison.Ordinal));
        Assert.That(finding, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finding!.Message, Does.Contain("keep the values"));
            Assert.That(finding!.Message, Does.Contain("ever recall them"));
            Assert.That(finding!.Message, Does.Not.Contain("recalled at the next Full Synchronisation"));
            Assert.That(finding!.MetaverseAttributeName, Is.EqualTo("Email"));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObjectOutOfScopeOfTheRule_IsNotPutToTheEngineAtAllAsync()
    {
        // A flow change cannot reach an object the rule does not manage. The engine would reach the same verdict
        // on its own, so the assertion is that the object is never put to it: every object costs TWO full
        // evaluations here, and paying that for a population the rule does not manage is the difference between a
        // preview that runs over a scoped subset and one that evaluates a whole system twice over.
        GivenRuleScopedToEngineering();
        var cso = GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local", firstName: "ada.lovelace@corp.local");

        var deltas = await EvaluateAsync(ProposalWritingEmailFrom(CsFirstNameAttributeId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Is.Empty);
            Assert.That(_syncRepo.RequestedConnectedSystemObjectIds, Does.Not.Contain(cso.Id));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposedExpressionFailsForOneObject_ReportsItRatherThanNoChangeAsync()
    {
        // The motivating case: an Expression that fails on a fraction of the population. Reported as no change,
        // the failing objects are indistinguishable from the ones the edit does not touch.
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local", firstName: "Ada");

        var deltas = await EvaluateAsync(ProposalWithExpression(
            "cs[\"givenName\"] + \".\" + cs[\"sn\"] + \"@corp.local\"", MissingInputBehaviour.FailMapping));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow));
    }

    // ── Counts ───────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CountImpactAsync_TwoObjectsWhoseValueChanges_CountsBothAsync()
    {
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local", firstName: "ada.lovelace@corp.local");
        GivenJoinedCso(email: "alan@corp.local", currentMetaverseEmail: "alan@corp.local", firstName: "alan.turing@corp.local");

        var counts = await NewAdapter().CountImpactAsync(Context(ProposalWritingEmailFrom(CsFirstNameAttributeId)));

        var flows = counts.SingleOrDefault(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(flows, Is.Not.Null);
        Assert.That(flows!.ObjectCount, Is.EqualTo(2));
    }

    [Test]
    public async Task EstimateCostAsync_CountsThePopulationTheWalkWouldReadAsync()
    {
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local");
        GivenJoinedCso(email: "alan@corp.local", currentMetaverseEmail: "alan@corp.local");

        var estimate = await NewAdapter().EstimateCostAsync(Context(ProposalWritingEmailFrom(CsFirstNameAttributeId)));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(2));
    }

    [Test]
    public async Task EstimateCostAsync_ProposalMatchesStoredMappings_IsZeroAsync()
    {
        GivenJoinedCso(email: "ada@corp.local", currentMetaverseEmail: "ada@corp.local");

        var estimate = await NewAdapter().EstimateCostAsync(Context(SyncRuleAttributeFlowProposal.FromCurrentMappings(_rule)));

        Assert.That(estimate.AffectedObjects, Is.EqualTo(0));
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EvaluateDeltasAsync_ExportMappingRetargeted_ReportsTheTargetsCurrentValueAsTheOldOneAsync()
    {
        // The export direction's hardest case, and the one a domain cutover actually is: the target already holds
        // what the rule writes today, so the STORED configuration stages nothing for the attribute and only the
        // proposal stages anything. Reading the staged changes alone would give "would now write X" with nothing to
        // compare X against; the engine's no-net-change skips (#1443) carry the value it declined to stage, which
        // is the target's current state and the old side of the pair.
        GivenExportRule();
        GivenMvoWithJoinedTargetObject(email: "ada@corp.local", alternateEmail: "ada.lovelace@corp.local",
            targetStoredEmail: "ada@corp.local");

        var deltas = await EvaluateAsync(ExportProposalWritingMailFrom(MvAlternateEmailAttributeId));

        var delta = deltas.SingleOrDefault(d => d.AttributeName == "mail");
        Assert.That(delta, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta!.OldValue, Is.EqualTo("ada@corp.local"),
                "the value the target holds today, recovered from the change the stored configuration declined to stage");
            Assert.That(delta!.NewValue, Is.EqualTo("ada.lovelace@corp.local"));
            Assert.That(delta!.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
            Assert.That(delta!.ConnectedSystemId, Is.EqualTo(SystemId));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ExportMappingUnchangedForAnObject_YieldsNoDeltaForItAsync()
    {
        // The counterpart: where both configurations would leave the target holding the same value, the two
        // no-net-change skips cancel and nothing is reported. Without that cancellation every object the rule
        // manages would appear in a preview of a change that does not touch it.
        GivenExportRule();
        GivenMvoWithJoinedTargetObject(email: "ada@corp.local", alternateEmail: "ada@corp.local",
            targetStoredEmail: "ada@corp.local");

        var deltas = await EvaluateAsync(ExportProposalWritingMailFrom(MvAlternateEmailAttributeId));

        Assert.That(deltas, Is.Empty);
    }

    // ── Contract ─────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Adapter_DeclaresTheAttributeFlowSurfaceAndItsProposalType()
    {
        var adapter = NewAdapter();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow));
            Assert.That(adapter.ProposalType, Is.EqualTo(typeof(SyncRuleAttributeFlowProposal)));
            Assert.That(adapter.ProducesDeltas, Is.True);
        }
    }

    #region helpers

    private SyncRuleAttributeFlowPreviewAdapter NewAdapter() => new(_jim, new SyncEngine());

    private PreviewContext Context(SyncRuleAttributeFlowProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow,
        ActivityId = Guid.CreateVersion7(),
        TargetId = RuleId,
        ProposedConfiguration = proposal
    };

    private async Task<List<PreviewDelta>> EvaluateAsync(SyncRuleAttributeFlowProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in NewAdapter().EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);
        return deltas;
    }

    private static SyncRuleMapping DirectMapping(SyncRule rule, int mappingId, MetaverseAttribute target,
        ConnectedSystemObjectTypeAttribute source, int priority = int.MaxValue)
    {
        var mapping = new SyncRuleMapping
        {
            Id = mappingId,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Priority = priority
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = mappingId,
            Order = 1,
            ConnectedSystemAttribute = source,
            ConnectedSystemAttributeId = source.Id
        });
        return mapping;
    }

    private static SyncRuleAttributeFlowProposal ProposalWritingEmailFrom(int connectedSystemAttributeId, int priority = int.MaxValue) =>
        new([new SyncRuleMappingProposal(
            MvEmailAttributeId,
            null,
            [new SyncRuleMappingSourceProposal(1, null, connectedSystemAttributeId)],
            Priority: priority)]);

    private static SyncRuleAttributeFlowProposal ExportProposalWritingMailFrom(int metaverseAttributeId) =>
        new([new SyncRuleMappingProposal(
            null,
            CsEmailAttributeId,
            [new SyncRuleMappingSourceProposal(1, metaverseAttributeId, null)])]);

    private static SyncRuleAttributeFlowProposal ProposalWithExpression(string expression, MissingInputBehaviour missingInputBehaviour) =>
        new([new SyncRuleMappingProposal(
            MvEmailAttributeId,
            null,
            [new SyncRuleMappingSourceProposal(1, null, null, expression, missingInputBehaviour)])]);

    /// <summary>
    /// An enabled import rule on a DIFFERENT Connected System that also writes the Email attribute, which is what
    /// makes the attribute contested and Attribute Priority live.
    /// </summary>
    private void GivenAnImportRuleOnAnotherSystemWritingEmail(int priority = int.MaxValue)
    {
        var otherRule = new SyncRule
        {
            Id = OtherSystemRuleId,
            Name = "Directory Import",
            ConnectedSystemId = OtherSystemId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectTypeId = MvoTypeId,
            MetaverseObjectType = _mvoType
        };
        otherRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 950,
            SyncRule = otherRule,
            SyncRuleId = otherRule.Id,
            TargetMetaverseAttribute = _mvEmail,
            TargetMetaverseAttributeId = MvEmailAttributeId,
            Priority = priority
        });
        _rules.Add(otherRule);
        _syncRepo.SeedSyncRule(otherRule);
    }

    /// <summary>
    /// Turns the fixture's rule into an export rule writing the target system's mail attribute from the Metaverse
    /// Email attribute.
    /// </summary>
    private void GivenExportRule()
    {
        _rule.Direction = SyncRuleDirection.Export;
        _rule.Name = "Directory Export";
        _rule.ProvisionToConnectedSystem = false;
        _rule.ProjectToMetaverse = false;
        _rule.ObjectMatchingRules = [];
        _rule.AttributeFlowRules.Clear();

        var mapping = new SyncRuleMapping
        {
            Id = MappingId,
            SyncRule = _rule,
            SyncRuleId = _rule.Id,
            TargetConnectedSystemAttribute = _csEmail,
            TargetConnectedSystemAttributeId = _csEmail.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = MappingId,
            Order = 1,
            MetaverseAttribute = _mvEmail,
            MetaverseAttributeId = _mvEmail.Id
        });
        _rule.AttributeFlowRules.Add(mapping);
    }

    private void GivenMvoWithJoinedTargetObject(string email, string alternateEmail, string? targetStoredEmail)
    {
        var mvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = _mvoType };
        AddValue(mvo, _mvEmail, email);
        AddValue(mvo, _mvAlternateEmail, alternateEmail);
        _mvos.Add(mvo);
        _syncRepo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemId = SystemId,
            TypeId = CsoTypeId,
            Type = _csoType,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        if (targetStoredEmail != null)
            AddValue(cso, _csEmail, targetStoredEmail);

        mvo.ConnectedSystemObjects.Add(cso);
        _csos.Add(cso);
        _syncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(MetaverseObject mvo, MetaverseAttribute attribute, string value) =>
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            MetaverseObject = mvo,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value
        });

    private void GivenRuleScopedToEngineering()
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = _csLastName,
            ConnectedSystemAttributeId = CsLastNameAttributeId,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Engineering"
        });
        _rule.ObjectScopingCriteriaGroups.Add(group);
    }

    private ConnectedSystemObject GivenJoinedCso(string email, string currentMetaverseEmail, string? firstName = null)
    {
        var mvo = new MetaverseObject { Id = Guid.CreateVersion7(), Type = _mvoType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            MetaverseObject = mvo,
            Attribute = _mvEmail,
            AttributeId = MvEmailAttributeId,
            StringValue = currentMetaverseEmail,
            ContributedBySystemId = SystemId,
            ContributedBySyncRuleId = RuleId
        });
        _mvos.Add(mvo);
        _syncRepo.SeedMetaverseObject(mvo);

        var cso = new ConnectedSystemObject
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemId = SystemId,
            TypeId = CsoTypeId,
            Type = _csoType,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };
        AddValue(cso, _csEmail, email);
        if (firstName != null)
            AddValue(cso, _csFirstName, firstName);

        _csos.Add(cso);
        _syncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string value) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.CreateVersion7(),
            ConnectedSystemObject = cso,
            Attribute = attribute,
            AttributeId = attribute.Id,
            StringValue = value
        });

    #endregion
}
