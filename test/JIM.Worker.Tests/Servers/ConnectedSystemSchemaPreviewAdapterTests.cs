// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Connected System schema selection adapter (#1475, #827 gap G6).
///
/// The whole reason this surface needs a preview is that its changes have no visible effect. Nothing fails, nothing
/// is deleted, nothing is disconnected; JIM simply stops reading, and everything downstream carries on over data
/// that has stopped moving. So the claims worth pinning are the ones an administrator could not check afterwards:
/// that deselecting an Object Type is reported as a freeze rather than the cascade the old copy promised (#1474),
/// that the objects concerned are the ones that actually hold a value for a deselected attribute rather than every
/// object of the type, and that the obsoletion toggle is measured against the objects already obsolete and still
/// joined, which are the only ones whose fate it changes now.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaPreviewAdapterTests
{
    private const int SystemId = 5;
    private const int UserTypeId = 9;
    private const int GroupTypeId = 11;
    private const int AnchorAttributeId = 100;
    private const int DisplayNameAttributeId = 101;
    private const int DepartmentAttributeId = 102;
    private const int MetaverseAttributeId = 201;
    private const int ImportRuleId = 42;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private JimApplication _jim = null!;
    private ConnectedSystemSchemaPreviewAdapter _adapter = null!;

    private ConnectedSystemObjectType _userType = null!;
    private ConnectedSystemObjectType _groupType = null!;
    private SyncRule _importRule = null!;

    private static readonly Guid FirstUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ObsoleteUser = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);

        _userType = new ConnectedSystemObjectType
        {
            Id = UserTypeId,
            Name = "User",
            ConnectedSystemId = SystemId,
            Selected = true,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = AnchorAttributeId, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = DisplayNameAttributeId, Name = "displayName", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = DepartmentAttributeId, Name = "department", Type = AttributeDataType.Text, Selected = true }
            ]
        };

        _groupType = new ConnectedSystemObjectType
        {
            Id = GroupTypeId,
            Name = "Group",
            ConnectedSystemId = SystemId,
            Selected = false,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 110, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true }
            ]
        };

        _importRule = new SyncRule
        {
            Id = ImportRuleId,
            Name = "Directory Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = UserTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [BuildMapping(DisplayNameAttributeId)]
        };

        _connectedSystemRepo.Setup(r => r.GetObjectTypesAsync(SystemId))
            .ReturnsAsync(() => [_userType, _groupType]);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(SystemId, It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => [_importRule]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(SystemId, It.IsAny<int>()))
            .ReturnsAsync(2);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(SystemId, UserTypeId))
            .ReturnsAsync([FirstUser, SecondUser]);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(SystemId, GroupTypeId))
            .ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, It.IsAny<int>()))
            .ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(SystemId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByIdsNoTrackingAsync(SystemId, It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((int _, IEnumerable<Guid> ids) => ids.Select(id => new ConnectedSystemObject
            {
                Id = id,
                ConnectedSystemId = SystemId,
                TypeId = UserTypeId,
                MetaverseObjectId = Guid.NewGuid()
            }).ToList());

        _jim = new JimApplication(_repo.Object);
        _adapter = new ConnectedSystemSchemaPreviewAdapter(_jim);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region contract

    [Test]
    public void Adapter_ServesTheSchemaSurfaceAndProducesDeltas()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ConnectedSystemSchema));
            Assert.That(_adapter.ProducesDeltas, Is.True);
            Assert.That(_adapter.ProposalType, Is.EqualTo(typeof(ConnectedSystemSchemaProposal)));
        }
    }

    [Test]
    public async Task Validate_AProposalMatchingTheStoredSchema_SaysNothingWouldChangeAsync()
    {
        var findings = await _adapter.ValidateAsync(Context(Stored()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings[0].Severity, Is.EqualTo(PreviewValidationSeverity.Information));
            Assert.That(findings[0].Message, Does.Contain("nothing would change"));
        }
    }

    [Test]
    public async Task EstimateCost_AProposalMatchingTheStoredSchema_CostsNothingAsync()
    {
        var estimate = await _adapter.EstimateCostAsync(Context(Stored()));

        Assert.That(estimate.AffectedObjects, Is.Zero);
    }

    [Test]
    public async Task EvaluateDeltas_AProposalMatchingTheStoredSchema_YieldsNothingAsync()
    {
        Assert.That(await DeltasAsync(Stored()), Is.Empty);
    }

    [Test]
    public async Task EstimateCost_EachLeverOnItsOwn_CostsTheObjectTypesPopulationAsync()
    {
        // Every Object Type the change walk yields differs in at least one of the three levers, because that is
        // what its comparison is over. Pinned per lever so the cost estimate cannot come to depend on a guard that
        // re-asks the question the walk already answered, and then quietly stop counting a lever nobody listed.
        var levers = new (string Name, Func<ConnectedSystemObjectTypeSelectionProposal, ConnectedSystemObjectTypeSelectionProposal> Edit)[]
        {
            ("the Object Type's own selection", type => type with { Selected = false }),
            ("the obsoletion recall toggle", type => type with { RemoveContributedAttributesOnObsoletion = false }),
            ("an attribute leaving the selection", type => type with { SelectedAttributeIds = [AnchorAttributeId] })
        };

        foreach (var (name, edit) in levers)
        {
            var estimate = await _adapter.EstimateCostAsync(Context(WithUserType(edit)));
            Assert.That(estimate.AffectedObjects, Is.EqualTo(2), $"moving {name} must cost the Object Type's population");
        }
    }

    #endregion

    #region object type selection

    [Test]
    public async Task Validate_DeselectingAnObjectType_ReportsTheFreezeAndNotACascadeAsync()
    {
        var findings = await _adapter.ValidateAsync(Context(WithUserType(type => type with { Selected = false })));
        var message = findings.Single(f => f.Message.StartsWith("Deselecting User", StringComparison.Ordinal)).Message;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("stay joined"),
                "the objects keep their Metaverse Object join, which is the part an administrator cannot see");
            Assert.That(message, Does.Contain("Nothing is obsoleted and nothing is deprovisioned"),
                "and the cascade has to be denied explicitly: the change is classified Destructive, so the " +
                "administrator will otherwise assume the usual one. See #1474");
            Assert.That(message, Does.Not.Contain("become obsolete"));
        }
    }

    [Test]
    public async Task Validate_DeselectingAnObjectTypeStillManagedByRules_NamesThemAsync()
    {
        var findings = await _adapter.ValidateAsync(Context(WithUserType(type => type with { Selected = false })));

        Assert.That(findings.Select(f => f.Message),
            Has.Some.Contains("Directory Import"),
            "a rule left running against frozen objects is the actionable half of the finding");
    }

    [Test]
    public async Task EvaluateDeltas_DeselectingAnObjectType_ReportsEveryLiveObjectAsFrozenAsync()
    {
        var deltas = await DeltasAsync(WithUserType(type => type with { Selected = false }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(2));
            Assert.That(deltas.Select(d => d.TransitionType),
                Is.All.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported));
            Assert.That(deltas.Select(d => d.ConnectedSystemObjectId), Is.EquivalentTo(new[] { FirstUser, SecondUser }));
            Assert.That(deltas[0].AttributeName, Is.Null,
                "the whole object stops being imported, so no single attribute is named");
        }
    }

    [Test]
    public async Task EvaluateDeltas_SelectingAnObjectType_ReportsItsObjectsResumingAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(SystemId, GroupTypeId))
            .ReturnsAsync([FirstUser]);

        var deltas = await DeltasAsync(WithGroupType(type => type with { Selected = true }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported));
        }
    }

    [Test]
    public async Task EvaluateDeltas_AnObjectTypeLeavingWithAttributesAlsoChanged_ReportsOnlyTheTypeLeavingAsync()
    {
        // Everything about the type stops being read, so a row per attribute beside that describes a detail of a
        // change the administrator has already been told the whole of.
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(SystemId, UserTypeId, DepartmentAttributeId))
            .ReturnsAsync([FirstUser]);

        var deltas = await DeltasAsync(WithUserType(type => type with
        {
            Selected = false,
            SelectedAttributeIds = [AnchorAttributeId, DisplayNameAttributeId]
        }));

        Assert.That(deltas.Select(d => d.AttributeName), Is.All.Null);
    }

    #endregion

    #region attribute selection

    [Test]
    public async Task EvaluateDeltas_DeselectingAnAttribute_ReportsOnlyTheObjectsHoldingAValueAsync()
    {
        // An object with no value for the attribute has nothing to freeze. Reporting the whole type would be a
        // confident number about objects the change does not touch.
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(SystemId, UserTypeId, DepartmentAttributeId))
            .ReturnsAsync([SecondUser]);

        var deltas = await DeltasAsync(WithUserType(type => type with
        {
            SelectedAttributeIds = [AnchorAttributeId, DisplayNameAttributeId]
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].ConnectedSystemObjectId, Is.EqualTo(SecondUser));
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported));
            Assert.That(deltas[0].AttributeName, Is.EqualTo("department"),
                "the attribute has to be named, because the object goes on being imported in every other respect");
        }
    }

    [Test]
    public async Task Validate_DeselectingAnAttributeAnAttributeFlowReads_NamesTheRuleAsync()
    {
        var findings = await _adapter.ValidateAsync(Context(WithUserType(type => type with
        {
            SelectedAttributeIds = [AnchorAttributeId, DepartmentAttributeId]
        })));

        Assert.That(findings.Select(f => f.Message), Has.Some.Contains("Directory Import"),
            "a mapping that goes on flowing frozen values is what makes an attribute deselection reach the Metaverse");
    }

    [Test]
    public async Task Validate_DeselectingTheExternalId_IsBlockingAsync()
    {
        var findings = await _adapter.ValidateAsync(Context(WithUserType(type => type with
        {
            SelectedAttributeIds = [DisplayNameAttributeId, DepartmentAttributeId]
        })));

        var blocking = findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocking, Has.Count.EqualTo(1));
            Assert.That(blocking[0].Message, Does.Contain("External ID"));
        }
    }

    #endregion

    #region obsoletion toggle

    [Test]
    public async Task EvaluateDeltas_TurningObsoletionRecallOff_ReportsTheObjectsAlreadyWaitingAsync()
    {
        // The population whose fate this changes NOW: objects already obsolete and still joined, waiting for the
        // synchronisation that will disconnect them. Future obsoletions cannot be counted, and pretending
        // otherwise would put a number against a population that does not exist yet.
        _connectedSystemRepo.Setup(r => r.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, UserTypeId))
            .ReturnsAsync([ObsoleteUser]);

        var deltas = await DeltasAsync(WithUserType(type => type with
        {
            RemoveContributedAttributesOnObsoletion = false
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues));
            Assert.That(deltas[0].ConnectedSystemObjectId, Is.EqualTo(ObsoleteUser));
        }
    }

    [Test]
    public async Task EvaluateDeltas_TurningObsoletionRecallOn_ReportsTheInverseAsync()
    {
        _userType.RemoveContributedAttributesOnObsoletion = false;
        _connectedSystemRepo.Setup(r => r.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, UserTypeId))
            .ReturnsAsync([ObsoleteUser]);

        var deltas = await DeltasAsync(WithUserType(type => type with
        {
            RemoveContributedAttributesOnObsoletion = true
        }));

        Assert.That(deltas.Single().TransitionType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues));
    }

    [Test]
    public async Task EvaluateDeltas_TheObsoletionToggleWithNothingWaiting_ReportsNothingAsync()
    {
        // No obsolete objects means no fate to change today, and saying so with an empty result is honest. The
        // finding is what tells the administrator the setting still applies to everything obsoleted from here on.
        var deltas = await DeltasAsync(WithUserType(type => type with
        {
            RemoveContributedAttributesOnObsoletion = false
        }));

        var findings = await _adapter.ValidateAsync(Context(WithUserType(type => type with
        {
            RemoveContributedAttributesOnObsoletion = false
        })));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Is.Empty);
            Assert.That(findings.Select(f => f.Message), Has.Some.Contains("leave the Metaverse values"));
        }
    }

    #endregion

    #region counts

    [Test]
    public async Task CountImpact_MatchesTheDeltasBehindItAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, UserTypeId))
            .ReturnsAsync([ObsoleteUser]);

        var proposal = WithUserType(type => type with { Selected = false, RemoveContributedAttributesOnObsoletion = false });
        var counts = await _adapter.CountImpactAsync(Context(proposal));
        var deltas = await DeltasAsync(proposal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Sum(c => c.ObjectCount), Is.EqualTo(deltas.Count),
                "a count that could disagree with the rows behind it is worse than no count");
            Assert.That(counts.Select(c => c.TransitionType), Is.EquivalentTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported,
                ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues
            }));
        }
    }

    [Test]
    public async Task EvaluateDeltas_AnObjectTypeThePayloadDoesNotMention_IsLeftAloneAsync()
    {
        // A truncated payload must not deselect by omission. The Group type is absent here, and it is selected in
        // the proposal's own terms only because the proposal says nothing about it.
        var proposal = new ConnectedSystemSchemaProposal(
            [Stored().For(UserTypeId)! with { Selected = false }]);

        var deltas = await DeltasAsync(proposal);

        Assert.That(deltas.Select(d => d.ObjectTypeName), Is.All.EqualTo("User"));
    }

    #endregion

    #region helpers

    /// <summary>
    /// An Attribute Flow mapping reading one Connected System attribute. Sources is read-only on the entity, so
    /// the collection is populated rather than assigned.
    /// </summary>
    private static SyncRuleMapping BuildMapping(int connectedSystemAttributeId)
    {
        var mapping = new SyncRuleMapping { TargetMetaverseAttributeId = MetaverseAttributeId };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = connectedSystemAttributeId });
        return mapping;
    }

    private PreviewContext Context(ConnectedSystemSchemaProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.ConnectedSystemSchema,
        ActivityId = Guid.NewGuid(),
        TargetId = SystemId,
        ProposedConfiguration = proposal
    };

    private ConnectedSystemSchemaProposal Stored() =>
        ConnectedSystemSchemaProposal.FromCurrentConfiguration([_userType, _groupType]);

    private ConnectedSystemSchemaProposal WithUserType(
        Func<ConnectedSystemObjectTypeSelectionProposal, ConnectedSystemObjectTypeSelectionProposal> edit) =>
        Replace(UserTypeId, edit);

    private ConnectedSystemSchemaProposal WithGroupType(
        Func<ConnectedSystemObjectTypeSelectionProposal, ConnectedSystemObjectTypeSelectionProposal> edit) =>
        Replace(GroupTypeId, edit);

    private ConnectedSystemSchemaProposal Replace(int objectTypeId,
        Func<ConnectedSystemObjectTypeSelectionProposal, ConnectedSystemObjectTypeSelectionProposal> edit) =>
        new(Stored().ObjectTypes
            .Select(objectType => objectType.ObjectTypeId == objectTypeId ? edit(objectType) : objectType)
            .ToList());

    private async Task<List<PreviewDelta>> DeltasAsync(ConnectedSystemSchemaProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);

        return deltas;
    }

    #endregion
}
