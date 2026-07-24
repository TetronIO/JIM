// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for SyncRuleScopingCriteriaDto round-trip mapping.
/// Guards against value-carrier fields silently dropping on the API boundary
/// — a regression would make Scenario 11's evaluation matrix meaningless.
/// </summary>
[TestFixture]
public class SyncRuleScopingCriteriaDtoTests
{
    [Test]
    public void FromEntity_PropagatesAllValueCarriers()
    {
        // Arrange: one entity that sets every typed value carrier so the mapping
        // can be checked field-by-field without per-type test duplication.
        var mvAttr = new MetaverseAttribute { Id = 5, Name = "Department", Type = AttributeDataType.Text };
        var entity = new SyncRuleScopingCriteria
        {
            Id = 42,
            MetaverseAttribute = mvAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance",
            IntValue = 100,
            LongValue = 5_000_000_000L,
            DecimalValue = 12345.6789m,
            DateTimeValue = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            BoolValue = true,
            GuidValue = new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CaseSensitive = false
        };

        // Act
        var dto = SyncRuleScopingCriteriaDto.FromEntity(entity);

        // Assert: every value carrier must survive the mapping. LongValue is the
        // field that was silently dropped before the gap-fix and is the regression
        // guard this test exists to catch.
        Assert.That(dto.Id, Is.EqualTo(42));
        Assert.That(dto.MetaverseAttributeId, Is.EqualTo(5));
        Assert.That(dto.MetaverseAttributeName, Is.EqualTo("Department"));
        Assert.That(dto.AttributeDataType, Is.EqualTo("Text"));
        Assert.That(dto.ComparisonType, Is.EqualTo("Equals"));
        Assert.That(dto.StringValue, Is.EqualTo("Finance"));
        Assert.That(dto.IntValue, Is.EqualTo(100));
        Assert.That(dto.LongValue, Is.EqualTo(5_000_000_000L));
        Assert.That(dto.DecimalValue, Is.EqualTo(12345.6789m));
        Assert.That(dto.DateTimeValue, Is.EqualTo(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(dto.BoolValue, Is.True);
        Assert.That(dto.GuidValue, Is.EqualTo(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));
        Assert.That(dto.CaseSensitive, Is.False);
    }

    [Test]
    public void FromEntity_LongValueOnly_OtherCarriersStayNull()
    {
        // Arrange: a LongNumber-typed criterion sets ONLY LongValue. The other
        // carriers must remain null in the DTO so callers can tell which was used.
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "EmployeeNumber", Type = AttributeDataType.LongNumber };
        var entity = new SyncRuleScopingCriteria
        {
            Id = 99,
            ConnectedSystemAttribute = csAttr,
            ComparisonType = SearchComparisonType.GreaterThan,
            LongValue = 4_000_000_000L
        };

        // Act
        var dto = SyncRuleScopingCriteriaDto.FromEntity(entity);

        // Assert
        Assert.That(dto.LongValue, Is.EqualTo(4_000_000_000L));
        Assert.That(dto.StringValue, Is.Null);
        Assert.That(dto.IntValue, Is.Null);
        Assert.That(dto.DecimalValue, Is.Null);
        Assert.That(dto.DateTimeValue, Is.Null);
        Assert.That(dto.BoolValue, Is.Null);
        Assert.That(dto.GuidValue, Is.Null);
        Assert.That(dto.AttributeDataType, Is.EqualTo("LongNumber"));
        Assert.That(dto.ConnectedSystemAttributeId, Is.EqualTo(7));
        Assert.That(dto.ComparisonType, Is.EqualTo("GreaterThan"));
    }

    [Test]
    public void FromEntity_DecimalValueOnly_OtherCarriersStayNull()
    {
        // Arrange: a Decimal-typed criterion sets ONLY DecimalValue. The other
        // carriers must remain null in the DTO so callers can tell which was used.
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 8, Name = "ContractedHours", Type = AttributeDataType.Decimal };
        var entity = new SyncRuleScopingCriteria
        {
            Id = 100,
            ConnectedSystemAttribute = csAttr,
            ComparisonType = SearchComparisonType.GreaterThan,
            DecimalValue = 37.5m
        };

        // Act
        var dto = SyncRuleScopingCriteriaDto.FromEntity(entity);

        // Assert
        Assert.That(dto.DecimalValue, Is.EqualTo(37.5m));
        Assert.That(dto.StringValue, Is.Null);
        Assert.That(dto.IntValue, Is.Null);
        Assert.That(dto.LongValue, Is.Null);
        Assert.That(dto.DateTimeValue, Is.Null);
        Assert.That(dto.BoolValue, Is.Null);
        Assert.That(dto.GuidValue, Is.Null);
        Assert.That(dto.AttributeDataType, Is.EqualTo("Decimal"));
        Assert.That(dto.ConnectedSystemAttributeId, Is.EqualTo(8));
        Assert.That(dto.ComparisonType, Is.EqualTo("GreaterThan"));
    }

    [Test]
    public void CreateScopingCriterionRequest_DefaultsCaseSensitiveTrue()
    {
        // The CaseSensitive flag defaults to true when omitted from the request,
        // matching the entity default. Scenario 11 relies on this so cells that
        // don't specify the flag get case-sensitive evaluation.
        var request = new CreateScopingCriterionRequest
        {
            ComparisonType = "Equals",
            StringValue = "Finance"
        };

        Assert.That(request.CaseSensitive, Is.True);
        Assert.That(request.LongValue, Is.Null);
        Assert.That(request.DecimalValue, Is.Null);
    }
}
