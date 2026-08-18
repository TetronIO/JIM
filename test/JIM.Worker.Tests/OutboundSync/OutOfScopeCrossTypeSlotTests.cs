// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for #1399. Two outbound Synchronisation Rules with disjoint scopes may target different
/// Connected System Object Types in one Connected System; every Metaverse Object is then out of scope
/// for one of the rules while the other's object correctly holds the system's one slot. Out-of-scope
/// evaluation encountering that cross-type slot must skip silently: deprovisioning an object is always
/// the duty of the rule targeting the object's own type, so the cross-type encounter is the normal
/// state of this configuration, not the #1331 misconfiguration. Before the fix, every Full
/// Synchronisation warned once per correctly provisioned object and raised
/// CouldNotExportDueToExistingConnectedSystemObject against the Activity, so a clean run could never
/// complete without warnings.
/// </summary>
[TestFixture]
public class OutOfScopeCrossTypeSlotTests
{
    private const int TargetSystemId = 6;

    private Mock<JimDbContext> _mockJimDbContext = null!;
    private JimApplication _jim = null!;
    private SyncRepository _syncRepo = null!;
    private ExportEvaluationServer _server = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockJimDbContext = new Mock<JimDbContext>();
        _syncRepo = TestUtilities.CreateSyncRepository();
        _jim = new JimApplication(new PostgresDataRepository(_mockJimDbContext.Object), syncRepository: _syncRepo);
        _server = new ExportEvaluationServer(_jim, _syncRepo);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private static ConnectedSystemObjectType ObjectType(int id, string name) =>
        new() { Id = id, Name = name, ConnectedSystemId = TargetSystemId };

    private static MetaverseObject Mvo() =>
        new() { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" } };

    /// <summary>
    /// An export rule whose scoping criterion the test MVOs can never satisfy, so every MVO is out of
    /// scope and the deprovisioning path is what gets evaluated.
    /// </summary>
    private static SyncRule OutOfScopeExportRule(ConnectedSystemObjectType targetType, string name)
    {
        var rule = new SyncRule
        {
            Id = 50,
            Name = name,
            ConnectedSystemId = TargetSystemId,
            ConnectedSystemObjectType = targetType,
            ConnectedSystemObjectTypeId = targetType.Id,
            Direction = SyncRuleDirection.Export,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    MetaverseAttribute = new MetaverseAttribute { Id = 900, Name = "Department", Type = AttributeDataType.Text },
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "OUT-OF-SCOPE-MARKER"
                }
            ]
        });
        return rule;
    }

    private static ConnectedSystemObject JoinedCso(ConnectedSystemObjectType type, MetaverseObject mvo)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystemId,
            Type = type,
            TypeId = type.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Projected,
            Status = ConnectedSystemObjectStatus.Normal
        };
        mvo.ConnectedSystemObjects.Add(cso);
        return cso;
    }

    private static ExportEvaluationCache CacheFor(MetaverseObject mvo, SyncRule rule, ConnectedSystemObject cso)
    {
        return new ExportEvaluationCache(
            exportRulesByMvoTypeId: new Dictionary<int, List<SyncRule>> { [mvo.Type!.Id] = [rule] },
            csoLookup: new Dictionary<(Guid, int), ConnectedSystemObject> { [(mvo.Id, TargetSystemId)] = cso },
            csoAttributeValues: Array.Empty<ConnectedSystemObjectAttributeValue>()
                .ToLookup(_ => (Guid.Empty, 0), av => av),
            targetSystemIds: [TargetSystemId]);
    }

    [Test]
    public async Task EvaluateOutOfScopeExports_SlotHeldByAnotherRulesObjectType_SkipsSilentlyAsync()
    {
        // The Scenario 16 shape: NaturalKeyAccount (Research) and GuidKeyedPerson (Finance) rules share
        // one Connected System with disjoint scopes. A Finance MVO is out of scope for the
        // NaturalKeyAccount rule while correctly holding a GuidKeyedPerson object in the slot.
        var naturalKeyType = ObjectType(25, "NaturalKeyAccount");
        var guidKeyedType = ObjectType(24, "GuidKeyedPerson");
        var mvo = Mvo();
        var existing = JoinedCso(guidKeyedType, mvo);
        var rule = OutOfScopeExportRule(naturalKeyType, "NaturalKeyAccount Export");

        var pendingExports = await _server.EvaluateOutOfScopeExportsAsync(mvo, CacheFor(mvo, rule, existing));

        using (Assert.EnterMultipleScope())
        {
            // A cross-type slot on the out-of-scope path is this configuration's normal state, not a
            // reportable conflict; one warning per correctly provisioned object per sync was noise (#1399).
            // The conflict-collection channel has been removed from this path entirely, so "no RPEI" is
            // structural; what remains observable is that nothing is staged and nothing is touched.
            Assert.That(pendingExports, Is.Empty,
                "Nothing to deprovision: the slot's object belongs to the other rule's Object Type.");
            Assert.That(existing.MetaverseObjectId, Is.EqualTo(mvo.Id),
                "The other rule's object must be left exactly as it was.");
        }
    }

    [Test]
    public async Task EvaluateOutOfScopeExports_SlotHeldByTheRulesOwnObjectType_DeprovisionsAsync()
    {
        // The rule's own object going out of scope is the case the deprovisioning path exists for.
        var naturalKeyType = ObjectType(25, "NaturalKeyAccount");
        var mvo = Mvo();
        var existing = JoinedCso(naturalKeyType, mvo);
        var rule = OutOfScopeExportRule(naturalKeyType, "NaturalKeyAccount Export");
        _syncRepo = TestUtilities.CreateSyncRepository(csos: [existing]);
        _server = new ExportEvaluationServer(_jim, _syncRepo);

        await _server.EvaluateOutOfScopeExportsAsync(mvo, CacheFor(mvo, rule, existing));

        Assert.That(existing.MetaverseObjectId, Is.Null,
            "OutboundDeprovisionAction.Disconnect must break the join for the rule's own type.");
    }
}
