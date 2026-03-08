using System;
using System.Collections.Generic;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for ObjectMatchingRule DTO mapping, including the new MetaverseObjectTypeId and SyncRuleId fields.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleDtoTests
{
    [Test]
    public void FromEntity_SimpleMode_MapsMetaverseObjectTypeFields()
    {
        // Arrange
        var mvoType = new MetaverseObjectType { Id = 42, Name = "User" };
        var objectType = new ConnectedSystemObjectType { Id = 10, Name = "HR_USER" };
        var targetAttr = new MetaverseAttribute { Id = 5, Name = "employeeId" };

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 0,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            MetaverseObjectTypeId = mvoType.Id,
            MetaverseObjectType = mvoType,
            TargetMetaverseAttributeId = targetAttr.Id,
            TargetMetaverseAttribute = targetAttr,
            CaseSensitive = false,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var dto = ObjectMatchingRuleDto.FromEntity(rule);

        // Assert
        Assert.That(dto.Id, Is.EqualTo(1));
        Assert.That(dto.MetaverseObjectTypeId, Is.EqualTo(42));
        Assert.That(dto.MetaverseObjectTypeName, Is.EqualTo("User"));
        Assert.That(dto.SyncRuleId, Is.Null);
        Assert.That(dto.ConnectedSystemObjectTypeId, Is.EqualTo(10));
        Assert.That(dto.ConnectedSystemObjectTypeName, Is.EqualTo("HR_USER"));
    }

    [Test]
    public void FromEntity_AdvancedMode_MapsSyncRuleId()
    {
        // Arrange
        var syncRule = new SyncRule { Id = 99, Name = "Import HR Users" };
        var targetAttr = new MetaverseAttribute { Id = 5, Name = "employeeId" };

        var rule = new ObjectMatchingRule
        {
            Id = 2,
            Order = 0,
            SyncRuleId = syncRule.Id,
            SyncRule = syncRule,
            TargetMetaverseAttributeId = targetAttr.Id,
            TargetMetaverseAttribute = targetAttr,
            CaseSensitive = true,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var dto = ObjectMatchingRuleDto.FromEntity(rule);

        // Assert
        Assert.That(dto.Id, Is.EqualTo(2));
        Assert.That(dto.SyncRuleId, Is.EqualTo(99));
        Assert.That(dto.MetaverseObjectTypeId, Is.Null);
        Assert.That(dto.MetaverseObjectTypeName, Is.Null);
        Assert.That(dto.ConnectedSystemObjectTypeId, Is.EqualTo(0), "No object type on advanced mode rule");
    }

    [Test]
    public void FromEntity_WithNullNavigationProperties_HandlesGracefully()
    {
        // Arrange - minimal rule with nullable navigation properties unset
        var rule = new ObjectMatchingRule
        {
            Id = 3,
            Order = 0,
            ConnectedSystemObjectTypeId = null,
            ConnectedSystemObjectType = null,
            MetaverseObjectTypeId = null,
            MetaverseObjectType = null,
            SyncRuleId = null,
            SyncRule = null,
            TargetMetaverseAttributeId = null,
            TargetMetaverseAttribute = null,
            Created = DateTime.UtcNow,
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var dto = ObjectMatchingRuleDto.FromEntity(rule);

        // Assert - should not throw
        Assert.That(dto.MetaverseObjectTypeId, Is.Null);
        Assert.That(dto.MetaverseObjectTypeName, Is.Null);
        Assert.That(dto.SyncRuleId, Is.Null);
        Assert.That(dto.ConnectedSystemObjectTypeName, Is.Null);
        Assert.That(dto.TargetMetaverseAttributeName, Is.Null);
    }

    [Test]
    public void FromEntity_MapsSources()
    {
        // Arrange
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "empNo" };
        var mvAttr = new MetaverseAttribute { Id = 20, Name = "employeeId" };

        var rule = new ObjectMatchingRule
        {
            Id = 4,
            Order = 0,
            Created = DateTime.UtcNow,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 100,
                    Order = 0,
                    ConnectedSystemAttributeId = csAttr.Id,
                    ConnectedSystemAttribute = csAttr
                },
                new()
                {
                    Id = 101,
                    Order = 1,
                    MetaverseAttributeId = mvAttr.Id,
                    MetaverseAttribute = mvAttr
                }
            }
        };

        // Act
        var dto = ObjectMatchingRuleDto.FromEntity(rule);

        // Assert
        Assert.That(dto.Sources, Has.Count.EqualTo(2));
        Assert.That(dto.Sources[0].ConnectedSystemAttributeId, Is.EqualTo(10));
        Assert.That(dto.Sources[0].ConnectedSystemAttributeName, Is.EqualTo("empNo"));
        Assert.That(dto.Sources[1].MetaverseAttributeId, Is.EqualTo(20));
        Assert.That(dto.Sources[1].MetaverseAttributeName, Is.EqualTo("employeeId"));
    }
}
