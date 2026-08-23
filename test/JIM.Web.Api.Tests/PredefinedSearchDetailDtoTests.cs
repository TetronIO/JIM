// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for a full Predefined Search (#1447). The raw entity carried the whole
/// MetaverseObjectType entity and unordered navigation collections; the DTO carries the target
/// type as id and name, orders attributes and criteria groups by Position, and reuses the
/// criteria-group DTO the criteria endpoints already speak.
/// </summary>
[TestFixture]
public class PredefinedSearchDetailDtoTests
{
    private static PredefinedSearch BuildSearch()
    {
        var displayName = new MetaverseAttribute { Id = 31, Name = "Display Name" };
        var employeeType = new MetaverseAttribute { Id = 32, Name = "Employee Type" };

        var search = new PredefinedSearch
        {
            Id = 4,
            Name = "All Permanent Staff",
            Uri = "permanent-staff",
            BuiltIn = true,
            IsEnabled = true,
            IsDefaultForMetaverseObjectType = false,
            MetaverseObjectType = new MetaverseObjectType { Id = 7, Name = "User" },
            Created = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc),
            CreatedByName = "System",
            LastUpdatedByName = "Jay",
            Attributes =
            [
                new PredefinedSearchAttribute { Id = 52, MetaverseAttribute = employeeType, Position = 1 },
                new PredefinedSearchAttribute { Id = 51, MetaverseAttribute = displayName, Position = 0 }
            ]
        };

        search.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
        {
            Id = 61,
            Type = SearchGroupType.All,
            Position = 1,
            Criteria =
            [
                new PredefinedSearchCriteria
                {
                    Id = 71,
                    MetaverseAttribute = employeeType,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Permanent"
                }
            ]
        });
        search.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup
        {
            Id = 62,
            Type = SearchGroupType.Any,
            Position = 0
        });

        return search;
    }

    [Test]
    public void FromEntity_MapsScalarsAndTargetTypeAsIdAndName()
    {
        var dto = PredefinedSearchDetailDto.FromEntity(BuildSearch());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(4));
            Assert.That(dto.Name, Is.EqualTo("All Permanent Staff"));
            Assert.That(dto.Uri, Is.EqualTo("permanent-staff"));
            Assert.That(dto.BuiltIn, Is.True);
            Assert.That(dto.IsEnabled, Is.True);
            Assert.That(dto.IsDefaultForMetaverseObjectType, Is.False);
            Assert.That(dto.MetaverseObjectTypeId, Is.EqualTo(7));
            Assert.That(dto.MetaverseObjectTypeName, Is.EqualTo("User"));
            Assert.That(dto.Created, Is.EqualTo(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc)));
            Assert.That(dto.LastUpdated, Is.EqualTo(new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc)));
            Assert.That(dto.CreatedByName, Is.EqualTo("System"));
            Assert.That(dto.LastUpdatedByName, Is.EqualTo("Jay"));
        }
    }

    [Test]
    public void FromEntity_OrdersAttributesByPositionWithNames()
    {
        var dto = PredefinedSearchDetailDto.FromEntity(BuildSearch());

        Assert.That(dto.Attributes.Select(a => (a.Position, a.MetaverseAttributeId, a.MetaverseAttributeName)),
            Is.EqualTo(new[] { (0, 31, "Display Name"), (1, 32, "Employee Type") }));
    }

    [Test]
    public void FromEntity_MapsCriteriaGroupsOrderedByPosition()
    {
        var dto = PredefinedSearchDetailDto.FromEntity(BuildSearch());

        Assert.That(dto.CriteriaGroups.Select(g => g.Id), Is.EqualTo(new[] { 62, 61 }));

        var allGroup = dto.CriteriaGroups.Single(g => g.Id == 61);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(allGroup.Type, Is.EqualTo("All"));
            Assert.That(allGroup.Criteria, Has.Count.EqualTo(1));
            Assert.That(allGroup.Criteria[0].MetaverseAttributeName, Is.EqualTo("Employee Type"));
            Assert.That(allGroup.Criteria[0].StringValue, Is.EqualTo("Permanent"));
        }
    }
}
