// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// Delta Sync must process every modified Connected System Object, however many pages they span, even when
/// processing an earlier page removes rows from the modified set.
/// <para>
/// Regression (Scenario 008 LeaverCohort at Scale200k10kGroups): Delta Sync paged its modified-since query by
/// OFFSET, and each page boundary deletes that page's obsolete objects. The set shrank under the offset, so every
/// other page was skipped: 1,000 of 2,000 obsolete target accounts were never processed, and the watermark then
/// moved past them, so only a Full Sync would ever pick them up. Full Sync was immune because it pages by keyset
/// cursor.
/// </para>
/// </summary>
[TestFixture]
public class DeltaSyncPagingWorkflowTests
{
    private const int PageSize = 2;
    private const int UserCount = 5;

    private WorkflowTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new WorkflowTestHarness();
    }

    [TearDown]
    public void TearDown()
    {
        _harness?.Dispose();
    }

    [Test]
    public async Task DeltaSync_ObsoleteObjectsSpanSeveralPages_ProcessesEveryOneAsync()
    {
        await SetSyncPageSizeAsync(PageSize);
        await SetUpSourceAsync();

        var sourceConnector = _harness.GetConnector("Source");
        sourceConnector.QueueImportObjects(GenerateUsers());
        await _harness.ExecuteFullImportAsync("Source");
        await _harness.ExecuteFullSyncAsync("Source");
        _harness.GetConnectedSystem("Source").LastSyncCompletedAt = DateTime.UtcNow;

        var afterInitialSync = await _harness.TakeSnapshotAsync("After Initial Sync");
        Assert.That(afterInitialSync.ConnectedSystemObjects["Source"], Has.Count.EqualTo(UserCount));

        // Every original user leaves the source and one new user joins: the Full Import marks all five leavers
        // obsolete (more than two pages' worth at a page size of 2). The joiner keeps the import non-empty, so
        // the empty-import safeguard does not refuse it.
        sourceConnector.QueueImportObjects(GenerateUsers(count: 1, startAt: UserCount + 1));
        await _harness.ExecuteFullImportAsync("Source");
        var afterImport = await _harness.TakeSnapshotAsync("After Import");
        Assert.That(afterImport.ConnectedSystemObjects["Source"].Count(c => c.Status == ConnectedSystemObjectStatus.Obsolete),
            Is.EqualTo(UserCount), "precondition: every user should be obsolete before the Delta Sync");

        await _harness.ExecuteDeltaSyncAsync("Source");

        var afterDeltaSync = await _harness.TakeSnapshotAsync("After Delta Sync");
        var remaining = afterDeltaSync.ConnectedSystemObjects.TryGetValue("Source", out var csos) ? csos : [];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Count(c => c.Status == ConnectedSystemObjectStatus.Obsolete), Is.Zero,
                "Delta Sync should have processed and removed every obsolete object; any left over were skipped by paging");
            Assert.That(remaining, Has.Count.EqualTo(1), "only the joiner should remain");
            Assert.That(remaining.Single().JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Projected),
                "the joiner should have been projected to the Metaverse");
        }
    }

    private async Task SetSyncPageSizeAsync(int pageSize)
    {
        _harness.DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = Constants.SettingKeys.SyncPageSize,
            DisplayName = "Sync page size",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "500",
            Value = pageSize.ToString()
        });
        await _harness.DbContext.SaveChangesAsync();
    }

    private async Task SetUpSourceAsync()
    {
        await _harness.CreateConnectedSystemAsync("Source");
        await _harness.CreateObjectTypeAsync("Source", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringAttribute("cn"));

        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithStringAttribute("cn"));

        var sourceUserCn = _harness.GetObjectType("Source", "User").Attributes.First(a => a.Name == "cn");
        var mvCn = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "cn");

        await _harness.CreateSyncRuleAsync(
            "Source User Import",
            "Source",
            "User",
            personType,
            SyncRuleDirection.Import,
            r => r
                .WithProjection()
                .WithAttributeFlow(mvCn, sourceUserCn));
    }

    private static List<ConnectedSystemImportObject> GenerateUsers(int count = UserCount, int startAt = 1) =>
        Enumerable.Range(startAt, count).Select(i => new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "User",
            Attributes =
            [
                new() { Name = "objectGUID", GuidValues = [Guid.NewGuid()] },
                new() { Name = "cn", StringValues = [$"user{i}"] }
            ]
        }).ToList();
}
