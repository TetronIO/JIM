// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// An import join's lookups are made once per page, not once per object (#2020). Measured at Scale500k25kGroups, the
/// per-object Object Matching lookup cost about 90 s of a 634.6 s first Full Synchronisation, of which the database
/// spent 4.0 s: nearly all of it round trips, one per object. These tests count the repository calls a page's joins
/// make and require that count not to grow with the number of objects on the page.
/// </summary>
[TestFixture]
public class JoinMatchBatchingWorkflowTests : WorkflowTestBase
{
    private CountingSyncRepository _counting = null!;

    [SetUp]
    public void SetUpCountingSyncRepo()
    {
        _counting = new CountingSyncRepository();
        _counting.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _counting;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task Synchronisation_PageOfUnjoinedObjects_MakesNoPerObjectJoinQueriesAsync(ConnectedSystemRunType runType)
    {
        var hr = await ArrangeAsync(objectCount: 12);

        _counting.Reset();
        await RunAsync(hr, runType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_counting.PerObjectLookups, Is.Zero, "a lookup per object");
            Assert.That(_counting.PerObjectLoads, Is.Zero, "a matched Metaverse Object loaded per object");
            Assert.That(_counting.PerObjectJoinCounts, Is.Zero, "a join count per object");
        }
    }

    [Test]
    public async Task FullSync_JoinQueriesPerPage_DoNotGrowWithTheObjectCountAsync()
    {
        var calls = new List<int>();
        foreach (var objectCount in new[] { 4, 24 })
        {
            TearDownAndRecreate();
            var hr = await ArrangeAsync(objectCount);
            _counting.Reset();
            await RunAsync(hr, ConnectedSystemRunType.FullSynchronisation);
            calls.Add(_counting.Total);
        }

        Assert.That(calls[1], Is.EqualTo(calls[0]),
            $"the join queries of one page grew from {calls[0]} to {calls[1]} when the page grew from 4 objects to 24");
    }

    [Test]
    public async Task FullSync_PageOfUnjoinedObjects_JoinsTheMatchedAndProjectsTheRestAsync()
    {
        var hr = await ArrangeAsync(objectCount: 12);

        await RunAsync(hr, ConnectedSystemRunType.FullSynchronisation);

        var hrObjects = SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == hr.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrObjects.Count(c => c.JoinType == ConnectedSystemObjectJoinType.Joined), Is.EqualTo(6), "the six with an identity join it");
            Assert.That(hrObjects.Count(c => c.JoinType == ConnectedSystemObjectJoinType.Projected), Is.EqualTo(6), "the six without project");
            Assert.That(SyncRepo.MetaverseObjects, Has.Count.EqualTo(12));
        }
    }

    /// <summary>
    /// The base fixture's database and repository again, for a second arrangement in the same test.
    /// </summary>
    private void TearDownAndRecreate()
    {
        BaseTearDown();
        BaseSetUp();
        SetUpCountingSyncRepo();
    }

    /// <summary>
    /// A Directory that has projected an identity for half of <paramref name="objectCount"/> Employee IDs, and an HR
    /// system holding an object for each Employee ID, unjoined, whose import rule matches on Employee ID and projects
    /// the objects it cannot join.
    /// </summary>
    private async Task<ConnectedSystem> ArrangeAsync(int objectCount)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        var directoryEmployeeIdAttr = directoryType.Attributes.First(a => a.Name == "EmployeeId");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeIdAttr, ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id } }
        });

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        hrImportRule.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = false,
            SyncRule = hrImportRule,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id }]
        });
        await DbContext.SaveChangesAsync();

        for (var i = 0; i < objectCount / 2; i++)
            await CreateCsoAsync(directory.Id, directoryType, $"Person {i}", $"EMP{i:000}");
        await RunAsync(directory, ConnectedSystemRunType.FullSynchronisation);
        Assert.That(SyncRepo.MetaverseObjects, Has.Count.EqualTo(objectCount / 2), "precondition: the Directory projects its accounts");

        for (var i = 0; i < objectCount; i++)
            await CreateCsoAsync(hr.Id, hrType, $"Person {i} (HR)", $"emp{i:000}");
        return hr;
    }

    private async Task RunAsync(ConnectedSystem connectedSystem, ConnectedSystemRunType runType)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, runType);
        if (runType == ConnectedSystemRunType.FullSynchronisation)
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
                .PerformFullSyncAsync();
        else
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
                .PerformDeltaSyncAsync();
    }

    /// <summary>Counts the repository calls an import join makes, per object and per page.</summary>
    private sealed class CountingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public int PerObjectLookups { get; private set; }
        public int PerObjectLoads { get; private set; }
        public int PerObjectJoinCounts { get; private set; }
        public int PageLookups { get; private set; }
        public int PageLoads { get; private set; }
        public int PageJoinCounts { get; private set; }

        public int Total => PerObjectLookups + PerObjectLoads + PerObjectJoinCounts + PageLookups + PageLoads + PageJoinCounts;

        public void Reset() =>
            PerObjectLookups = PerObjectLoads = PerObjectJoinCounts = PageLookups = PageLoads = PageJoinCounts = 0;

        public override Task<IReadOnlyList<Guid>> FindMetaverseObjectIdsByMatchValuesAsync(
            int metaverseObjectTypeId, int metaverseAttributeId, AttributeDataType valueType, IReadOnlyList<object> values, bool caseSensitive, int take)
        {
            PerObjectLookups++;
            return base.FindMetaverseObjectIdsByMatchValuesAsync(metaverseObjectTypeId, metaverseAttributeId, valueType, values, caseSensitive, take);
        }

        public override Task<MetaverseObject?> GetMetaverseObjectForJoinMatchAsync(Guid metaverseObjectId)
        {
            PerObjectLoads++;
            return base.GetMetaverseObjectForJoinMatchAsync(metaverseObjectId);
        }

        public override Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
        {
            PerObjectJoinCounts++;
            return base.GetConnectedSystemObjectCountByMvoAsync(connectedSystemId, metaverseObjectId);
        }

        public override Task<IReadOnlyList<(object Value, Guid MetaverseObjectId)>> FindMetaverseObjectIdsByEachMatchValueAsync(
            int metaverseObjectTypeId, int metaverseAttributeId, AttributeDataType valueType, IReadOnlyList<object> values, bool caseSensitive, int takePerValue)
        {
            PageLookups++;
            return base.FindMetaverseObjectIdsByEachMatchValueAsync(metaverseObjectTypeId, metaverseAttributeId, valueType, values, caseSensitive, takePerValue);
        }

        public override Task<IReadOnlyDictionary<Guid, MetaverseObject>> GetMetaverseObjectsForJoinMatchAsync(IReadOnlyCollection<Guid> metaverseObjectIds)
        {
            PageLoads++;
            return base.GetMetaverseObjectsForJoinMatchAsync(metaverseObjectIds);
        }

        public override Task<IReadOnlyDictionary<Guid, int>> GetConnectedSystemObjectCountsByMvoAsync(int connectedSystemId, IReadOnlyCollection<Guid> metaverseObjectIds)
        {
            PageJoinCounts++;
            return base.GetConnectedSystemObjectCountsByMvoAsync(connectedSystemId, metaverseObjectIds);
        }
    }
}
