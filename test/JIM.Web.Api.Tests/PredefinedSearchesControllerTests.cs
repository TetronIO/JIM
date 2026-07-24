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
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the admin Predefined Searches management API.
/// </summary>
[TestFixture]
public class PredefinedSearchesControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISearchRepository> _mockSearchRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<ILogger<PredefinedSearchesController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private PredefinedSearchesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSearchRepo = new Mock<ISearchRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Search).Returns(_mockSearchRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        // Every write path now records a configuration-change Activity (see SearchServer); stub the two calls it
        // always makes so the pre-existing CRUD tests (which predate that behaviour) keep exercising just the
        // repository mutation they assert on, without asserting on Activity attribution themselves. The dedicated
        // ChangeReason-attribution tests live in PredefinedSearchChangeHistoryApiTests.
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockLogger = new Mock<ILogger<PredefinedSearchesController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new PredefinedSearchesController(_mockLogger.Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static PredefinedSearch BuildSearch(int id, bool isEnabled) => new()
    {
        Id = id,
        Name = "People",
        Uri = "people",
        IsEnabled = isEnabled,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
    };

    [Test]
    public async Task GetByIdAsync_WithExistingId_ReturnsOkWithEntityAsync()
    {
        var existing = BuildSearch(7, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(7)).ReturnsAsync(existing);

        var result = await _controller.GetByIdAsync(7);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.SameAs(existing));
    }

    [Test]
    public async Task GetByIdAsync_WithUnknownId_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(999)).ReturnsAsync((PredefinedSearch?)null);

        var result = await _controller.GetByIdAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetByUriAsync_WithExistingUri_ReturnsOkWithEntityAsync()
    {
        var existing = BuildSearch(7, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync("people")).ReturnsAsync(existing);

        var result = await _controller.GetByUriAsync("people");

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.SameAs(existing));
    }

    [Test]
    public async Task GetByUriAsync_WithUnknownUri_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync("nope")).ReturnsAsync((PredefinedSearch?)null);

        var result = await _controller.GetByUriAsync("nope");

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetByUriAsync_WithEmptyUri_ReturnsBadRequestAsync()
    {
        var result = await _controller.GetByUriAsync(string.Empty);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockSearchRepo.Verify(r => r.GetPredefinedSearchAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllHeadersIncludingDisabledAsync()
    {
        var headers = new List<PredefinedSearchHeader>
        {
            new() { Id = 1, Name = "People", Uri = "people", IsEnabled = true, MetaverseObjectTypeName = "Person" },
            new() { Id = 2, Name = "Security Groups", Uri = "security-groups", IsEnabled = false, MetaverseObjectTypeName = "Group" }
        };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchHeadersAsync()).ReturnsAsync(headers);

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.InstanceOf<IList<PredefinedSearchHeader>>());
        var value = (IList<PredefinedSearchHeader>)okResult.Value!;
        Assert.That(value.Count, Is.EqualTo(2));
        Assert.That(value.Any(h => !h.IsEnabled), Is.True, "Disabled searches must be included for admin discovery.");
    }

    [Test]
    public async Task UpdateAsync_WithIsEnabledTrue_MutatesAndPersistsEntityAsync()
    {
        var existing = BuildSearch(42, isEnabled: false);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest { IsEnabled = true });

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.True);
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithIsEnabledFalse_MutatesAndPersistsEntityAsync()
    {
        var existing = BuildSearch(42, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest { IsEnabled = false });

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.False);
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithNullFields_LeavesEntityUntouchedAsync()
    {
        var existing = BuildSearch(42, isEnabled: true);
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(42)).ReturnsAsync(existing);

        var result = await _controller.UpdateAsync(42, new UpdatePredefinedSearchRequest());

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(existing.IsEnabled, Is.True, "IsEnabled was not provided in the request and must not be mutated.");
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(existing), Times.Once);
    }

    [Test]
    public async Task UpdateAsync_WithUnknownId_ReturnsNotFoundAndDoesNotSaveAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(999)).ReturnsAsync((PredefinedSearch?)null);

        var result = await _controller.UpdateAsync(999, new UpdatePredefinedSearchRequest { IsEnabled = true });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>()), Times.Never);
    }

    // ─── Criteria groups and criteria ───

    private const int SearchId = 7;
    private const int GroupId = 11;
    private const int ObjectTypeId = 1;

    private static PredefinedSearch BuildSearchWithGroup(params PredefinedSearchCriteria[] criteria)
    {
        var search = BuildSearch(SearchId, isEnabled: true);
        var group = new PredefinedSearchCriteriaGroup { Id = GroupId, Type = SearchGroupType.All, Position = 0 };
        group.Criteria.AddRange(criteria);
        search.CriteriaGroups.Add(group);
        return search;
    }

    private MetaverseAttribute SetUpAttribute(int id, string name, AttributeDataType type, bool onObjectType = true)
    {
        var attribute = new MetaverseAttribute
        {
            Id = id,
            Name = name,
            Type = type,
            MetaverseObjectTypes = onObjectType
                ? new List<MetaverseObjectType> { new() { Id = ObjectTypeId, Name = "Person" } }
                : new List<MetaverseObjectType>()
        };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(id, It.IsAny<bool>())).ReturnsAsync(attribute);
        return attribute;
    }

    [Test]
    public async Task GetCriteriaGroupsAsync_WithExistingSearch_ReturnsGroupsAsync()
    {
        var search = BuildSearchWithGroup(new PredefinedSearchCriteria { Id = 1, ComparisonType = SearchComparisonType.Equals, StringValue = "x" });
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);

        var result = await _controller.GetCriteriaGroupsAsync(SearchId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var groups = (List<PredefinedSearchCriteriaGroupDto>)((OkObjectResult)result).Value!;
        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0].Id, Is.EqualTo(GroupId));
        Assert.That(groups[0].Criteria, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetCriteriaGroupsAsync_WithUnknownSearch_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync((PredefinedSearch?)null);

        var result = await _controller.GetCriteriaGroupsAsync(SearchId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CreateCriteriaGroupAsync_WithValidType_ReturnsCreatedAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(BuildSearch(SearchId, true));
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriteriaGroupAsync(SearchId, null, SearchGroupType.Any, 2))
            .ReturnsAsync(new PredefinedSearchCriteriaGroup { Id = 99, Type = SearchGroupType.Any, Position = 2 });

        var result = await _controller.CreateCriteriaGroupAsync(SearchId, new CreatePredefinedSearchCriteriaGroupRequest { Type = "Any", Position = 2 });

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (PredefinedSearchCriteriaGroupDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Type, Is.EqualTo("Any"));
    }

    [Test]
    public async Task CreateCriteriaGroupAsync_WithInvalidType_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(BuildSearch(SearchId, true));

        var result = await _controller.CreateCriteriaGroupAsync(SearchId, new CreatePredefinedSearchCriteriaGroupRequest { Type = "Maybe" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateChildCriteriaGroupAsync_WithValidParent_ReturnsCreatedAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriteriaGroupAsync(SearchId, GroupId, SearchGroupType.Any, 0))
            .ReturnsAsync(new PredefinedSearchCriteriaGroup { Id = 50, Type = SearchGroupType.Any });

        var result = await _controller.CreateChildCriteriaGroupAsync(SearchId, GroupId, new CreatePredefinedSearchCriteriaGroupRequest { Type = "Any" });

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (PredefinedSearchCriteriaGroupDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Type, Is.EqualTo("Any"));
    }

    [Test]
    public async Task CreateChildCriteriaGroupAsync_WithUnknownParent_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());

        var result = await _controller.CreateChildCriteriaGroupAsync(SearchId, groupId: 999, new CreatePredefinedSearchCriteriaGroupRequest { Type = "Any" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.CreatePredefinedSearchCriteriaGroupAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<SearchGroupType>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteCriteriaGroupAsync_WithUnknownGroup_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());

        var result = await _controller.DeleteCriteriaGroupAsync(SearchId, groupId: 999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.DeletePredefinedSearchCriteriaGroupAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task CreateCriterionAsync_WithTextEquals_ReturnsCreatedAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(20, "Department", AttributeDataType.Text);
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 5; return c; });

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 20, ComparisonType = "Equals", StringValue = "Finance" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (PredefinedSearchCriteriaDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.StringValue, Is.EqualTo("Finance"));
        Assert.That(dto.AttributeDataType, Is.EqualTo("Text"));
    }

    [Test]
    public async Task CreateCriterionAsync_WithNumberGreaterThan_ReturnsCreatedAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(21, "MemberCount", AttributeDataType.Number);
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 6; return c; });

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 21, ComparisonType = "GreaterThan", IntValue = 0 };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (PredefinedSearchCriteriaDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.IntValue, Is.EqualTo(0));
    }

    [Test]
    public async Task CreateCriterionAsync_WithDecimalGreaterThan_ReturnsCreatedAndPersistsExactValueAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(24, "AnnualSalary", AttributeDataType.Decimal);
        PredefinedSearchCriteria? captured = null;
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 9; captured = c; return c; });

        // A high-precision value that a double cannot represent exactly, proving the value
        // is never routed through double/float on the way to persistence.
        const decimal highPrecisionValue = 12345678901234567.89m;
        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 24, ComparisonType = "GreaterThan", DecimalValue = highPrecisionValue };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DecimalValue, Is.EqualTo(highPrecisionValue));
        var dto = (PredefinedSearchCriteriaDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.DecimalValue, Is.EqualTo(highPrecisionValue));
        Assert.That(dto.AttributeDataType, Is.EqualTo("Decimal"));
    }

    [Test]
    public async Task CreateCriterionAsync_WithDecimalMissingValueCarrier_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(24, "AnnualSalary", AttributeDataType.Decimal);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 24, ComparisonType = "GreaterThan" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(error.Message, Is.EqualTo("DecimalValue is required for a Decimal criterion."));
        _mockSearchRepo.Verify(r => r.CreatePredefinedSearchCriterionAsync(It.IsAny<int>(), It.IsAny<PredefinedSearchCriteria>()), Times.Never);
    }

    [Test]
    public async Task CreateCriterionAsync_WithTextOperatorOnDecimalAttribute_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(24, "AnnualSalary", AttributeDataType.Decimal);

        // Contains is a text-only operator; invalid for a Decimal attribute.
        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 24, ComparisonType = "Contains", DecimalValue = 1.5m };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockSearchRepo.Verify(r => r.CreatePredefinedSearchCriterionAsync(It.IsAny<int>(), It.IsAny<PredefinedSearchCriteria>()), Times.Never);
    }

    [Test]
    public async Task CreateCriterionAsync_WithDateTimeLessThan_NormalisesToUtcAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(22, "AccountExpiry", AttributeDataType.DateTime);
        PredefinedSearchCriteria? captured = null;
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 7; captured = c; return c; });

        var request = new PredefinedSearchCriterionRequest
        {
            MetaverseAttributeId = 22,
            ComparisonType = "LessThan",
            DateTimeValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
        };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DateTimeValue!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task CreateCriterionAsync_WithBooleanEquals_ReturnsCreatedAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(23, "IsActive", AttributeDataType.Boolean);
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 8; return c; });

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 23, ComparisonType = "Equals", BoolValue = true };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithOperatorNotApplicableToType_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(21, "MemberCount", AttributeDataType.Number);

        // StartsWith is a text-only operator; invalid for a Number attribute.
        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 21, ComparisonType = "StartsWith", IntValue = 1 };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockSearchRepo.Verify(r => r.CreatePredefinedSearchCriterionAsync(It.IsAny<int>(), It.IsAny<PredefinedSearchCriteria>()), Times.Never);
    }

    [Test]
    public async Task CreateCriterionAsync_WithMissingValueCarrier_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(21, "MemberCount", AttributeDataType.Number);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 21, ComparisonType = "GreaterThan" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithInvalidComparisonType_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(20, "Department", AttributeDataType.Text);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 20, ComparisonType = "Sideways", StringValue = "x" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithAttributeNotOnObjectType_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(30, "Foreign", AttributeDataType.Text, onObjectType: false);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 30, ComparisonType = "Equals", StringValue = "x" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithUnknownAttribute_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(404, It.IsAny<bool>())).ReturnsAsync((MetaverseAttribute?)null);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 404, ComparisonType = "Equals", StringValue = "x" };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithRelativeDate_ReturnsCreatedAndPersistsRelativeFieldsAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(22, "AccountExpiry", AttributeDataType.DateTime);
        PredefinedSearchCriteria? captured = null;
        _mockSearchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((int _, PredefinedSearchCriteria c) => { c.Id = 9; captured = c; return c; });

        var request = new PredefinedSearchCriterionRequest
        {
            MetaverseAttributeId = 22,
            ComparisonType = "LessThanOrEquals",
            ValueMode = "Relative",
            RelativeCount = 7,
            RelativeUnit = "Days",
            RelativeDirection = "FromNow"
        };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.ValueMode, Is.EqualTo(DateCriteriaValueMode.Relative));
        Assert.That(captured.RelativeCount, Is.EqualTo(7));
        Assert.That(captured.RelativeUnit, Is.EqualTo(RelativeDateUnit.Days));
        Assert.That(captured.RelativeDirection, Is.EqualTo(RelativeDateDirection.FromNow));
        Assert.That(captured.DateTimeValue, Is.Null);
    }

    [Test]
    public async Task CreateCriterionAsync_WithRelativeOnNonDateAttribute_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(20, "Department", AttributeDataType.Text);

        var request = new PredefinedSearchCriterionRequest
        {
            MetaverseAttributeId = 20,
            ComparisonType = "Equals",
            ValueMode = "Relative",
            RelativeCount = 7,
            RelativeUnit = "Days",
            RelativeDirection = "FromNow"
        };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockSearchRepo.Verify(r => r.CreatePredefinedSearchCriterionAsync(It.IsAny<int>(), It.IsAny<PredefinedSearchCriteria>()), Times.Never);
    }

    [Test]
    public async Task CreateCriterionAsync_WithRelativeMissingUnit_ReturnsBadRequestAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(22, "AccountExpiry", AttributeDataType.DateTime);

        var request = new PredefinedSearchCriterionRequest
        {
            MetaverseAttributeId = 22,
            ComparisonType = "LessThanOrEquals",
            ValueMode = "Relative",
            RelativeCount = 7,
            RelativeDirection = "FromNow"
        };
        var result = await _controller.CreateCriterionAsync(SearchId, GroupId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateCriterionAsync_WithUnknownGroup_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 20, ComparisonType = "Equals", StringValue = "x" };
        var result = await _controller.CreateCriterionAsync(SearchId, groupId: 999, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateCriterionAsync_WithValidRequest_ReturnsOkAsync()
    {
        var existing = new PredefinedSearchCriteria { Id = 5, ComparisonType = SearchComparisonType.Equals, StringValue = "old" };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup(existing));
        SetUpAttribute(20, "Department", AttributeDataType.Text);
        _mockSearchRepo.Setup(r => r.UpdatePredefinedSearchCriterionAsync(It.IsAny<PredefinedSearchCriteria>()))
            .ReturnsAsync((PredefinedSearchCriteria c) => c);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 20, ComparisonType = "Contains", StringValue = "new" };
        var result = await _controller.UpdateCriterionAsync(SearchId, GroupId, criterionId: 5, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (PredefinedSearchCriteriaDto)((OkObjectResult)result).Value!;
        Assert.That(dto.ComparisonType, Is.EqualTo("Contains"));
        Assert.That(dto.StringValue, Is.EqualTo("new"));
    }

    [Test]
    public async Task UpdateCriterionAsync_WithUnknownCriterion_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());
        SetUpAttribute(20, "Department", AttributeDataType.Text);

        var request = new PredefinedSearchCriterionRequest { MetaverseAttributeId = 20, ComparisonType = "Equals", StringValue = "x" };
        var result = await _controller.UpdateCriterionAsync(SearchId, GroupId, criterionId: 999, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.UpdatePredefinedSearchCriterionAsync(It.IsAny<PredefinedSearchCriteria>()), Times.Never);
    }

    [Test]
    public async Task DeleteCriterionAsync_WithExistingCriterion_ReturnsNoContentAsync()
    {
        var existing = new PredefinedSearchCriteria { Id = 5, ComparisonType = SearchComparisonType.Equals, StringValue = "x" };
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup(existing));
        _mockSearchRepo.Setup(r => r.DeletePredefinedSearchCriterionAsync(5)).ReturnsAsync(true);

        var result = await _controller.DeleteCriterionAsync(SearchId, GroupId, criterionId: 5);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockSearchRepo.Verify(r => r.DeletePredefinedSearchCriterionAsync(5), Times.Once);
    }

    [Test]
    public async Task DeleteCriterionAsync_WithUnknownCriterion_ReturnsNotFoundAsync()
    {
        _mockSearchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(BuildSearchWithGroup());

        var result = await _controller.DeleteCriterionAsync(SearchId, GroupId, criterionId: 999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSearchRepo.Verify(r => r.DeletePredefinedSearchCriterionAsync(It.IsAny<int>()), Times.Never);
    }
}
