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
/// When an export rule's scope disconnects a Metaverse Object's Connected System Object, whether that was the
/// object's last connector decides whether its deletion grace period starts. The answer must come from what is
/// joined to it, not from the object's in-memory <c>ConnectedSystemObjects</c> collection, which holds only the
/// connectors this run happens to have loaded alongside it (#2598): a page loads its Metaverse Objects without
/// their other systems' connectors, so a collection-based answer marks a person whose HR account is still joined
/// as having lost their last connector, and deletes them once the grace period ends.
/// </summary>
[TestFixture]
public class LastConnectorDisconnectionTests
{
    private const int HrSystemId = 5;
    private const int DirectorySystemId = 6;

    private Mock<JimDbContext> _mockJimDbContext = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockJimDbContext = new Mock<JimDbContext>();
        _jim = new JimApplication(new PostgresDataRepository(_mockJimDbContext.Object), syncRepository: TestUtilities.CreateSyncRepository());
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task EvaluateOutOfScopeExports_AnotherSystemStillJoinedButNotLoaded_DoesNotMarkLastConnectorDisconnectedAsync()
    {
        var mvo = ProjectedPerson();
        var directoryAccount = JoinedCso(DirectorySystemId, mvo);
        var hrRecord = JoinedCso(HrSystemId, mvo);

        // The page loaded the Metaverse Object with its directory account only: the HR record is joined in the
        // store but is not in the in-memory collection.
        mvo.ConnectedSystemObjects.Remove(hrRecord);
        var server = ServerOver(TestUtilities.CreateSyncRepository(csos: [directoryAccount, hrRecord]));

        await server.EvaluateOutOfScopeExportsAsync(mvo, CacheFor(mvo, directoryAccount));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(directoryAccount.MetaverseObjectId, Is.Null, "The out-of-scope directory account is disconnected.");
            Assert.That(mvo.LastConnectorDisconnectedDate, Is.Null,
                "The HR record is still joined, so this was not the last connector and no grace period starts.");
        }
    }

    [Test]
    public async Task EvaluateOutOfScopeExports_NothingElseJoined_MarksLastConnectorDisconnectedAsync()
    {
        var mvo = ProjectedPerson();
        var directoryAccount = JoinedCso(DirectorySystemId, mvo);
        var server = ServerOver(TestUtilities.CreateSyncRepository(csos: [directoryAccount]));

        await server.EvaluateOutOfScopeExportsAsync(mvo, CacheFor(mvo, directoryAccount));

        Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null,
            "Disconnecting the only connector of a Projected object whose type deletes on last disconnect starts its grace period.");
    }

    private ExportEvaluationServer ServerOver(SyncRepository syncRepository) => new(_jim, syncRepository);

    private static MetaverseObject ProjectedPerson() => new()
    {
        Id = Guid.NewGuid(),
        Origin = MetaverseObjectOrigin.Projected,
        Type = new MetaverseObjectType
        {
            Id = 1, Name = "User", DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
        }
    };

    private static ConnectedSystemObject JoinedCso(int connectedSystemId, MetaverseObject mvo)
    {
        var type = new ConnectedSystemObjectType { Id = 20 + connectedSystemId, Name = "user", ConnectedSystemId = connectedSystemId };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Type = type,
            TypeId = type.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal
        };
        mvo.ConnectedSystemObjects.Add(cso);
        return cso;
    }

    /// <summary>
    /// The directory's export rule, with a scoping criterion no test object meets, so the object is out of scope
    /// and its account is disconnected.
    /// </summary>
    private static ExportEvaluationCache CacheFor(MetaverseObject mvo, ConnectedSystemObject directoryAccount)
    {
        var rule = new SyncRule
        {
            Id = 50,
            Name = "Directory Export",
            ConnectedSystemId = DirectorySystemId,
            ConnectedSystemObjectType = directoryAccount.Type,
            ConnectedSystemObjectTypeId = directoryAccount.TypeId,
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

        return new ExportEvaluationCache(
            exportRulesByMvoTypeId: new Dictionary<int, List<SyncRule>> { [mvo.Type.Id] = [rule] },
            csoLookup: new Dictionary<(Guid, int), ConnectedSystemObject> { [(mvo.Id, DirectorySystemId)] = directoryAccount },
            csoAttributeValues: Array.Empty<ConnectedSystemObjectAttributeValue>().ToLookup(_ => (Guid.Empty, 0), av => av),
            targetSystemIds: [DirectorySystemId]);
    }
}
