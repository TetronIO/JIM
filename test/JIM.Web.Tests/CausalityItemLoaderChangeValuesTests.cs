// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using JIM.Web.Causality;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How <see cref="CausalityItemLoader"/> reads an item's change values (#2498): a summary of every change, the rows of the
/// small ones read whole in one window per side, and nothing more of a large group's change, whose rows the panel reads
/// a window at a time where they are shown.
/// </summary>
[TestFixture]
public class CausalityItemLoaderChangeValuesTests
{
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly Guid RecordChangeId = Guid.NewGuid();
    private static readonly Guid IdentityChangeId = Guid.NewGuid();
    private static readonly Guid LargeSnapshotId = Guid.NewGuid();

    private readonly List<ChangeValueRowQuery> _connectedSystemQueries = [];
    private readonly List<ChangeValueRowQuery> _metaverseQueries = [];
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _connectedSystemQueries.Clear();
        _metaverseQueries.Clear();
        var repository = new Mock<IRepository>();
        var activities = new Mock<IActivityRepository>();
        activities.Setup(r => r.GetFollowUpActivitiesAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        var metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Activity).Returns(activities.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(metaverse.Object);

        var item = new ActivityRunProfileExecutionItem
        {
            Id = ItemId,
            ActivityId = Guid.NewGuid(),
            ConnectedSystemObjectChange = new ConnectedSystemObjectChange { Id = RecordChangeId },
            MetaverseObjectChange = new MetaverseObjectChange { Id = IdentityChangeId }
        };
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ConnectedSystemObjectChangeId = LargeSnapshotId
        });
        activities.Setup(r => r.GetActivityRunProfileExecutionItemDetailAsync(ItemId)).ReturnsAsync(item);
        // The item has outcomes, so the loader walks its causes too: none are recorded here.
        activities.Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync([]);
        activities.Setup(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync(new Dictionary<Guid, CausalChainItemSummary>());

        connectedSystems
            .Setup(r => r.GetCsoChangeValueSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([new(RecordChangeId, 2, 1, 0, null, null), new(LargeSnapshotId, 0, 600_007, 0, null, null)]);
        metaverse
            .Setup(r => r.GetMvoChangeValueSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([new(IdentityChangeId, 1, 0, 0, null, null)]);
        connectedSystems
            .Setup(r => r.GetCsoChangeValueRowsAsync(It.IsAny<ChangeValueRowQuery>()))
            .Callback<ChangeValueRowQuery>(_connectedSystemQueries.Add)
            .ReturnsAsync(new RangeResultSet<CsoChangeValueRow>
            {
                Results = Enumerable.Range(0, 3).Select(i => new CsoChangeValueRow
                {
                    ChangeId = RecordChangeId, AttributeName = $"attribute{i}", Operation = ChangeValueOperation.Set,
                    Value = new CsoValueChangeDto { ValueChangeType = ValueChangeType.Add, StringValue = $"value {i}" }
                }).ToList()
            });
        metaverse
            .Setup(r => r.GetMvoChangeValueRowsAsync(It.IsAny<ChangeValueRowQuery>()))
            .Callback<ChangeValueRowQuery>(_metaverseQueries.Add)
            .ReturnsAsync(new RangeResultSet<MvoChangeValueRow>
            {
                Results =
                [
                    new MvoChangeValueRow
                    {
                        ChangeId = IdentityChangeId, AttributeName = "department", Operation = ChangeValueOperation.Set,
                        Value = new MvoValueChangeDto { ValueChangeType = ValueChangeType.Add, StringValue = "Finance" }
                    }
                ]
            });

        _jim = new JimApplication(repository.Object);
    }

    [Test]
    public async Task LoadAsync_ReadsSmallChangesWholeAndLeavesALargeGroupsChangeToBeReadAWindowAtATimeAsync()
    {
        var data = await CausalityItemLoader.LoadAsync(_jim, ItemId);

        var changeValues = data!.ChangeValues;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changeValues.Summaries.Keys, Is.EquivalentTo(new[] { RecordChangeId, IdentityChangeId, LargeSnapshotId }));
            Assert.That(_connectedSystemQueries.Single().ChangeIds, Is.EqualTo(new[] { RecordChangeId }),
                "the large snapshot is never read with the item");
            Assert.That(_connectedSystemQueries.Single().Count, Is.EqualTo(3), "one window holds every row of the small changes");
            Assert.That(_metaverseQueries.Single().ChangeIds, Is.EqualTo(new[] { IdentityChangeId }),
                "each side's changes are read through their own side");
            Assert.That(changeValues.Rows[RecordChangeId], Has.Count.EqualTo(3));
            Assert.That(changeValues.Rows[IdentityChangeId].Single().Value, Is.EqualTo("Finance"));
            Assert.That(changeValues.Rows.ContainsKey(LargeSnapshotId), Is.False);
        }
    }
}
