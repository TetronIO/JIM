// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class HistoryControllerChangeSearchTests
{
    private Mock<IMetaverseRepository> _metaverse = null!;
    private HistoryController _controller = null!;
    private MetaverseObjectChangeSearchQuery? _queried;
    private int _queriedOffset;
    private int _queriedCount;

    [SetUp]
    public void SetUp()
    {
        _queried = null;
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        _controller = new HistoryController(new Mock<ILogger<HistoryController>>().Object, new JimApplication(repository.Object));

        _metaverse
            .Setup(r => r.GetMetaverseAttributeAsync(3, false))
            .ReturnsAsync(new MetaverseAttribute { Id = 3, Name = "Employee Status", Type = AttributeDataType.Text });
    }

    private void SetupResults(params MetaverseObjectChangeSearchResult[] results)
    {
        _metaverse
            .Setup(r => r.SearchMetaverseObjectChangesRangeAsync(It.IsAny<MetaverseObjectChangeSearchQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<MetaverseObjectChangeSearchQuery, int, int, bool>((q, offset, count, _) =>
            {
                _queried = q;
                _queriedOffset = offset;
                _queriedCount = count;
            })
            .ReturnsAsync(new RangeResultSet<MetaverseObjectChangeSearchResult> { Results = results.ToList(), TotalResults = 41 });
    }

    [Test]
    public async Task SearchMetaverseObjectChangesAsync_PassesCriteriaAndWindowsByPageAsync()
    {
        SetupResults();
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var request = new MetaverseObjectChangeSearchRequest
        {
            FromDate = from,
            ObjectTypeId = 2,
            DisplayNameSearch = "Ana",
            MatchMode = AttributeConditionMatchMode.Any,
            Conditions = [new() { AttributeId = 3, Operator = AttributeChangeOperator.ChangedTo, Value = "Disabled" }],
            Page = 3,
            PageSize = 20
        };

        var result = await _controller.SearchMetaverseObjectChangesAsync(request) as OkObjectResult;
        var response = result!.Value as PaginatedResponse<MetaverseObjectChangeSearchItem>;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queriedOffset, Is.EqualTo(40));
            Assert.That(_queriedCount, Is.EqualTo(20));
            Assert.That(_queried!.FromDate, Is.EqualTo(from));
            Assert.That(_queried!.ObjectTypeId, Is.EqualTo(2));
            Assert.That(_queried!.DisplayNameSearch, Is.EqualTo("Ana"));
            Assert.That(_queried!.MatchMode, Is.EqualTo(AttributeConditionMatchMode.Any));
            Assert.That(_queried!.Conditions.Single().Match!.Text, Is.EqualTo("Disabled"));
            Assert.That(response!.TotalCount, Is.EqualTo(41));
            Assert.That(response!.Page, Is.EqualTo(3));
            Assert.That(response!.PageSize, Is.EqualTo(20));
        }
    }

    [Test]
    public async Task SearchMetaverseObjectChangesAsync_MapsEachChangeWithItsValuesAsync()
    {
        var changeId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        SetupResults(new MetaverseObjectChangeSearchResult
        {
            MetaverseObjectId = objectId,
            MetaverseObjectDeleted = true,
            MetaverseObjectDisplayName = "Carol",
            MetaverseObjectTypeId = 2,
            MetaverseObjectTypeName = "Person",
            MetaverseObjectDeletedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            MatchedAttributeIds = [3],
            Change = new MvoChangeHistoryDto
            {
                Id = changeId,
                ChangeTime = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
                ChangeType = ObjectChangeType.AttributeFlow,
                InitiatedByType = JIM.Models.Activities.ActivityInitiatorType.System,
                InitiatedByName = "System",
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
                SyncRuleName = "HR Inbound",
                ActivityId = activityId,
                AttributeChanges =
                [
                    new MvoAttributeChangeDto
                    {
                        AttributeId = 3,
                        AttributeName = "Employee Status",
                        ValueChanges =
                        [
                            new MvoValueChangeDto { ValueChangeType = ValueChangeType.Add, StringValue = "Disabled" },
                            new MvoValueChangeDto { ValueChangeType = ValueChangeType.Remove, StringValue = "Active" }
                        ]
                    },
                    new MvoAttributeChangeDto
                    {
                        AttributeId = 4,
                        AttributeName = "Manager",
                        ValueChanges =
                        [
                            new MvoValueChangeDto
                            {
                                ValueChangeType = ValueChangeType.Add,
                                ReferenceValue = new MvoChangeReferenceDto { Id = managerId, DisplayName = "Ana Ruiz" }
                            }
                        ]
                    }
                ]
            }
        });

        var result = await _controller.SearchMetaverseObjectChangesAsync(new MetaverseObjectChangeSearchRequest()) as OkObjectResult;
        var item = (result!.Value as PaginatedResponse<MetaverseObjectChangeSearchItem>)!.Items.Single();
        var status = item.AttributeChanges.Single(a => a.AttributeName == "Employee Status");
        var manager = item.AttributeChanges.Single(a => a.AttributeName == "Manager");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.Id, Is.EqualTo(changeId));
            Assert.That(item.ChangeType, Is.EqualTo(ObjectChangeType.AttributeFlow));
            Assert.That(item.MetaverseObjectId, Is.EqualTo(objectId));
            Assert.That(item.MetaverseObjectDeleted, Is.True);
            Assert.That(item.MetaverseObjectDisplayName, Is.EqualTo("Carol"));
            Assert.That(item.MetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(item.ActivityId, Is.EqualTo(activityId));
            Assert.That(item.MetaverseObjectDeletedAt, Is.EqualTo(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)));
            Assert.That(item.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.SynchronisationRule));
            Assert.That(item.SyncRuleName, Is.EqualTo("HR Inbound"));
            Assert.That(status.Matched, Is.True);
            Assert.That(status.AddedValues, Is.EqualTo(new[] { "Disabled" }));
            Assert.That(status.RemovedValues, Is.EqualTo(new[] { "Active" }));
            Assert.That(manager.Matched, Is.False);
            Assert.That(manager.AddedValues, Is.EqualTo(new[] { "Ana Ruiz" }));
        }
    }

    [Test]
    public async Task SearchMetaverseObjectChangesAsync_ConditionThatCannotBeSearched_ReturnsBadRequestWithTheReasonAsync()
    {
        SetupResults();
        var request = new MetaverseObjectChangeSearchRequest { Conditions = [new() { AttributeId = 99 }] };

        var result = await _controller.SearchMetaverseObjectChangesAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_queried, Is.Null);
    }

    [Test]
    public async Task SearchMetaverseObjectChangesAsync_PageBeyondTheRetrievalDepth_ReturnsBadRequestAsync()
    {
        SetupResults();

        var result = await _controller.SearchMetaverseObjectChangesAsync(new MetaverseObjectChangeSearchRequest { Page = 999_999, PageSize = 100 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_queried, Is.Null);
        }
    }

    [Test]
    public async Task SearchMetaverseObjectChangesAsync_PageSizeAboveTheLimit_IsClampedAsync()
    {
        SetupResults();

        await _controller.SearchMetaverseObjectChangesAsync(new MetaverseObjectChangeSearchRequest { Page = 0, PageSize = 5000 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queriedOffset, Is.EqualTo(0));
            Assert.That(_queriedCount, Is.EqualTo(PaginationLimits.MaxPageSize));
        }
    }
}
