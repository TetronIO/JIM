// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The Full Import memory harness (#2521, Phase 0): a Full Import over N Connector pages, each holding a large group and
/// its members, and what the import holds at once while it runs. The counter,
/// <see cref="SyncImportTaskProcessor.PeakHeldAttributeValues"/>, is the most attribute values held on Connected System
/// Objects at any of its sampling points (after each page is processed, before the save phase, after each save batch);
/// it is deterministic, so it can be asserted where heap measurements cannot.
/// <para>
/// Today the import holds every object it read until a single save phase at the end, so the counter grows with N. The
/// first test characterises that, and is meant to fail the day it stops being true: Phase 5 stages each unit as it
/// arrives, and inverts it into "the counter is flat in N". Characterised rather than marked explicit so that nothing can
/// make it flat by accident unnoticed.
/// </para>
/// </summary>
[TestFixture]
public class FullImportHeldValuesHarnessTests : WorkflowTestBase
{
    private const int MembersPerGroup = 40;

    /// <summary>
    /// A page holds each member's External ID and name, and the group's External ID, name and every member; the run holds
    /// every page's until the save phase, so the most held at once is that times the pages (Phase 5 makes it one unit's).
    /// One Connected System per run, so the page counts are separate cases rather than two runs of one test.
    /// </summary>
    [TestCase(1)]
    [TestCase(4)]
    public async Task FullImport_PagesOfLargeGroups_HoldsEveryPagesValuesAtOnceAsync(int pageCount)
    {
        var processor = await RunAsync(pageCount, measure: true);

        Assert.That(processor.PeakHeldAttributeValues, Is.EqualTo(pageCount * (MembersPerGroup * 2 + 2 + MembersPerGroup)));
    }

    [Test]
    public async Task FullImport_NotMeasuring_RecordsNothingAsync()
    {
        var processor = await RunAsync(pageCount: 2, measure: false);

        Assert.That(processor.PeakHeldAttributeValues, Is.Zero, "the counter costs nothing unless a harness asks for it");
    }

    /// <summary>
    /// The managed heap after a forced collection as each page is handed over, for 1 and 5 times the pages (two pages and
    /// ten), each of a 500-member group: the measurement beside the counter. Explicit, because a heap figure depends on the
    /// runtime and on what else the process holds, so it is read rather than asserted; Phase 5's success criterion is that
    /// the two runs end within 25% of each other.
    /// </summary>
    [TestCase(2)]
    [TestCase(10)]
    [Explicit("A memory measurement for the bounded-memory Full Import work (#2521); read its output, nothing is asserted.")]
    public async Task FullImport_PagesOfLargeGroups_MeasuresTheHeapPerPageAsync(int pageCount)
    {
        var heapByPage = new List<long>();
        var processor = await RunAsync(pageCount, measure: true, membersPerGroup: 500,
            beforePage: _ => heapByPage.Add(GC.GetTotalMemory(forceFullCollection: true)));
        var afterRun = GC.GetTotalMemory(forceFullCollection: true);

        TestContext.Out.WriteLine($"{pageCount} pages: peak held values {processor.PeakHeldAttributeValues:N0}; heap before each page (MB): " +
            string.Join(", ", heapByPage.Select(bytes => (bytes / 1024d / 1024d).ToString("N1"))) + $"; after the run: {afterRun / 1024d / 1024d:N1} MB");
    }

    private async Task<SyncImportTaskProcessor> RunAsync(int pageCount, bool measure, int membersPerGroup = MembersPerGroup, Action<int>? beforePage = null)
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var user = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var group = await CreateCsoTypeAsync(connectedSystem.Id, "group",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute
                { Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        ]);

        var pages = Enumerable.Range(0, pageCount).Select(page => PageOfOneGroup(user, group, page, membersPerGroup)).ToList();

        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };
        var processor = new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages, beforePage: beforePage), connectedSystem, runProfile, workerTask, new CancellationTokenSource())
        {
            MeasureHeldAttributeValues = measure
        };

        await processor.PerformImportAsync();
        return processor;
    }

    /// <summary>A page holding <paramref name="membersPerGroup"/> users and one group with every one of them as a member.</summary>
    private static List<ConnectedSystemImportObject> PageOfOneGroup(ConnectedSystemObjectType user, ConnectedSystemObjectType group, int page,
        int membersPerGroup)
    {
        var memberIds = Enumerable.Range(0, membersPerGroup).Select(_ => Guid.NewGuid()).ToList();
        var objects = memberIds.Select((id, index) => new ConnectedSystemImportObject
        {
            ObjectType = user.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [id] },
                new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"User {page}.{index}"] }
            ]
        }).ToList();

        objects.Add(new ConnectedSystemImportObject
        {
            ObjectType = group.Name,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [Guid.NewGuid()] },
                new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [$"Group {page}"] },
                new ConnectedSystemImportObjectAttribute
                    { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = [.. memberIds.Select(id => id.ToString())] }
            ]
        });
        return objects;
    }
}
