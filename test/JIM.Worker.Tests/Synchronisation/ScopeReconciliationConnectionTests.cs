// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Temporal Scope Reconciliation for an export rule that also has a connection criterion (#2021): the sweep reads the
/// candidates' connections, so it can decide their scope rather than count every one as undecidable.
/// </summary>
[TestFixture]
public class ScopeReconciliationConnectionTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task ReconcileAsync_ExportRuleWithAConnectionCriterion_DecidesTheCandidatesScopeAsync()
    {
        TestUtilities.SetEnvironmentVariables();
        var start = new MetaverseAttribute { Id = 31, Name = "Start Date", Type = AttributeDataType.DateTime };
        var person = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [start] };
        var hrUser = new ConnectedSystemObjectType { Id = 70, Name = "User", ConnectedSystemId = 7 };
        var rule = new SyncRule { Id = 5, Name = "Badge Export", Enabled = true, Direction = SyncRuleDirection.Export, ConnectedSystemId = 9, MetaverseObjectTypeId = person.Id, MetaverseObjectType = person };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                // In scope from 14 days before the start date, for people with an HR record.
                new SyncRuleScopingCriteria
                {
                    MetaverseAttribute = start, MetaverseAttributeId = start.Id, ComparisonType = SearchComparisonType.LessThanOrEquals,
                    ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 14, RelativeUnit = RelativeDateUnit.Days, RelativeDirection = RelativeDateDirection.FromNow
                },
                new SyncRuleScopingCriteria { ConnectedObjectType = hrUser, ConnectedObjectTypeId = hrUser.Id, ComparisonType = SearchComparisonType.HasValue }
            ]
        });

        var joiner = new MetaverseObject { Id = Guid.NewGuid(), Type = person };
        joiner.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = joiner, Attribute = start, AttributeId = start.Id, DateTimeValue = Now.AddDays(10) });

        var syncRepo = new JIM.InMemoryData.SyncRepository();
        syncRepo.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = hrUser.ConnectedSystemId, TypeId = hrUser.Id, Type = hrUser,
            MetaverseObjectId = joiner.Id, Status = ConnectedSystemObjectStatus.Normal
        });

        var repo = new Mock<IRepository>();
        var csRepo = new Mock<IConnectedSystemRepository>();
        var mvRepo = new Mock<IMetaverseRepository>();
        repo.Setup(r => r.ConnectedSystems).Returns(csRepo.Object);
        repo.Setup(r => r.Metaverse).Returns(mvRepo.Object);
        csRepo.Setup(r => r.GetSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync([rule]);
        mvRepo.Setup(r => r.GetMetaverseObjectIdsByDateAttributeRangeAsync(person.Id, start.Id, It.IsAny<DateTime?>(), It.IsAny<DateTime>()))
            .ReturnsAsync([joiner.Id]);
        mvRepo.Setup(r => r.GetMetaverseObjectsByIdsNoTrackingAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync([joiner]);
        var flagged = new List<Guid>();
        mvRepo.Setup(r => r.MarkMetaverseObjectsScopeEvaluatedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), Now))
            .Callback((IReadOnlyCollection<Guid> _, IReadOnlyCollection<Guid> f, DateTime _) => flagged.AddRange(f))
            .Returns(Task.CompletedTask);

        var result = await new JimApplication(repo.Object, syncRepository: syncRepo).ScopeReconciliation.ReconcileAsync(Now.AddHours(-1), Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UndecidableObjects, Is.Zero, "the joiner's connections were read");
            Assert.That(flagged, Is.EqualTo(new[] { joiner.Id }), "in scope and not yet provisioned, so flagged for review");
        }
    }
}
