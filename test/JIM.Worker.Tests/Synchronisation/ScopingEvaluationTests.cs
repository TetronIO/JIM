// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for the ScopingEvaluationServer which handles scoping criteria evaluation
/// for both export (MVO) and import (CSO) Synchronisation Rules.
/// </summary>
[TestFixture]
public class ScopingEvaluationTests
{
    private ScopingEvaluationServer _scopingEvaluation = null!;

    [SetUp]
    public void SetUp()
    {
        _scopingEvaluation = new ScopingEvaluationServer();
    }

    #region Invalid operator/type hardening

    [Test]
    public void IsMvoInScopeForExportRule_InvalidOperatorForAttributeType_ThrowsInvalidOperationException()
    {
        // Arrange: a DateTime attribute scoped with a text operator ("Starts With"). Such a criterion should
        // never persist, but if one reaches the evaluator it must fail loudly rather than silently mis-scope.
        var accountExpires = new MetaverseAttribute { Id = 9, Name = "Account Expires", Type = AttributeDataType.DateTime };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 9,
            Attribute = accountExpires,
            DateTimeValue = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = accountExpires,
            ComparisonType = SearchComparisonType.StartsWith
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule));
        Assert.That(ex!.Message, Does.Contain("Account Expires"));
    }

    [Test]
    public void IsCsoInScopeForImportRule_InvalidOperatorForAttributeType_ThrowsInvalidOperationException()
    {
        // Arrange: import-side equivalent, a DateTime attribute scoped with "Contains".
        var accountExpires = new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "accountExpires", Type = AttributeDataType.DateTime };
        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 9,
            Attribute = accountExpires,
            DateTimeValue = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = accountExpires,
            ComparisonType = SearchComparisonType.Contains
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule));
        Assert.That(ex!.Message, Does.Contain("accountExpires"));
    }

    #endregion

    #region Export (MVO) Scoping Tests

    [Test]
    public void IsMvoInScopeForExportRule_NoScopingCriteria_ReturnsTrue()
    {
        // Arrange
        var mvo = CreateTestMvo();
        var exportRule = CreateExportSyncRule();
        // No scoping criteria added

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "MVO should be in scope when no scoping criteria exist");
    }

    [Test]
    public void IsMvoInScopeForExportRule_StringEqualsMatch_ReturnsTrue()
    {
        // Arrange
        var departmentAttr = new MetaverseAttribute { Id = 1, Name = "Department", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = departmentAttr,
            StringValue = "IT"
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "MVO should be in scope when Department equals 'IT'");
    }

    [Test]
    public void IsMvoInScopeForExportRule_StringEqualsNoMatch_ReturnsFalse()
    {
        // Arrange
        var departmentAttr = new MetaverseAttribute { Id = 1, Name = "Department", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = departmentAttr,
            StringValue = "Finance"
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.False, "MVO should not be in scope when Department is 'Finance' but criteria requires 'IT'");
    }

    [Test]
    public void IsMvoInScopeForExportRule_StringEqualsCaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var departmentAttr = new MetaverseAttribute { Id = 1, Name = "Department", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = departmentAttr,
            StringValue = "it" // lowercase
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT", // uppercase
            CaseSensitive = false // Explicitly set to case-insensitive
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "String comparison should be case-insensitive when CaseSensitive=false");
    }

    [Test]
    public void IsMvoInScopeForExportRule_StringEqualsCaseSensitive_ReturnsFalse()
    {
        // Arrange - test that default case-sensitive behavior works
        var departmentAttr = new MetaverseAttribute { Id = 1, Name = "Department", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = departmentAttr,
            StringValue = "it" // lowercase
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT" // uppercase - default CaseSensitive=true
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.False, "String comparison should be case-sensitive by default");
    }

    [Test]
    public void IsMvoInScopeForExportRule_StringStartsWith_ReturnsTrue()
    {
        // Arrange
        var titleAttr = new MetaverseAttribute { Id = 2, Name = "Title", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 2,
            Attribute = titleAttr,
            StringValue = "Senior Developer"
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = titleAttr,
            ComparisonType = SearchComparisonType.StartsWith,
            StringValue = "Senior"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "MVO should be in scope when Title starts with 'Senior'");
    }

    [Test]
    public void IsMvoInScopeForExportRule_BooleanEquals_ReturnsTrue()
    {
        // Arrange
        var activeAttr = new MetaverseAttribute { Id = 3, Name = "Active", Type = AttributeDataType.Boolean };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 3,
            Attribute = activeAttr,
            BoolValue = true
        });

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = activeAttr,
            ComparisonType = SearchComparisonType.Equals,
            BoolValue = true
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "MVO should be in scope when Active equals true");
    }

    [Test]
    public void IsMvoInScopeForExportRule_MultipleGroupsWithOrLogic_ReturnsTrue()
    {
        // Arrange
        var departmentAttr = new MetaverseAttribute { Id = 1, Name = "Department", Type = AttributeDataType.Text };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = departmentAttr,
            StringValue = "HR"
        });

        var exportRule = CreateExportSyncRule();

        // Group 1: Department = IT (won't match)
        var group1 = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group1.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group1);

        // Group 2: Department = HR (will match)
        var group2 = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group2.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = departmentAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "HR"
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group2);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "MVO should be in scope when any top-level group matches (OR logic)");
    }

    #endregion

    #region Import (CSO) Scoping Tests

    [Test]
    public void IsCsoInScopeForImportRule_NoScopingCriteria_ReturnsTrue()
    {
        // Arrange
        var cso = CreateTestCso();
        var importRule = CreateImportSyncRule();
        // No scoping criteria added

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when no scoping criteria exist");
    }

    [Test]
    public void IsCsoInScopeForImportRule_StringEqualsMatch_ReturnsTrue()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = ouAttr,
            StringValue = "Finance"
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when ou equals 'Finance'");
    }

    [Test]
    public void IsCsoInScopeForImportRule_StringEqualsNoMatch_ReturnsFalse()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = ouAttr,
            StringValue = "IT"
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.False, "CSO should not be in scope when ou is 'IT' but criteria requires 'Finance'");
    }

    [Test]
    public void IsCsoInScopeForImportRule_StringContains_ReturnsTrue()
    {
        // Arrange
        var dnAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text };
        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 2,
            Attribute = dnAttr,
            StringValue = "CN=John Smith,OU=Finance,DC=example,DC=com"
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = dnAttr,
            ComparisonType = SearchComparisonType.Contains,
            StringValue = "OU=Finance"
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when DN contains 'OU=Finance'");
    }

    [Test]
    public void IsCsoInScopeForImportRule_NumberGreaterThan_ReturnsTrue()
    {
        // Arrange
        var priorityAttr = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "priority", Type = AttributeDataType.Number };
        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 3,
            Attribute = priorityAttr,
            IntValue = 10
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = priorityAttr,
            ComparisonType = SearchComparisonType.GreaterThan,
            IntValue = 5
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when priority > 5");
    }

    [Test]
    public void IsCsoInScopeForImportRule_MultipleCriteriaWithAndLogic_AllMatch_ReturnsTrue()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var activeAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "enabled", Type = AttributeDataType.Boolean };

        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = ouAttr,
            StringValue = "Finance"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 2,
            Attribute = activeAttr,
            BoolValue = true
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = activeAttr,
            ComparisonType = SearchComparisonType.Equals,
            BoolValue = true
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when all criteria in AND group match");
    }

    [Test]
    public void IsCsoInScopeForImportRule_MultipleCriteriaWithAndLogic_PartialMatch_ReturnsFalse()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var activeAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "enabled", Type = AttributeDataType.Boolean };

        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = ouAttr,
            StringValue = "Finance"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 2,
            Attribute = activeAttr,
            BoolValue = false // Doesn't match criteria
        });

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = activeAttr,
            ComparisonType = SearchComparisonType.Equals,
            BoolValue = true
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.False, "CSO should not be in scope when only some criteria in AND group match");
    }

    [Test]
    public void IsCsoInScopeForImportRule_MissingAttribute_ReturnsFalse()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var cso = CreateTestCso();
        // No attribute values - ou is missing

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.False, "CSO should not be in scope when required attribute is missing");
    }

    [Test]
    public void IsCsoInScopeForImportRule_WrongRuleDirection_ReturnsFalse()
    {
        // Arrange
        var cso = CreateTestCso();
        var exportRule = CreateExportSyncRule(); // Wrong direction!

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, exportRule);

        // Assert
        Assert.That(result, Is.False, "Should return false when called with non-import rule");
    }

    #endregion

    #region Edge Cases

    [Test]
    public void IsMvoInScopeForExportRule_WrongRuleDirection_ReturnsFalse()
    {
        // Arrange
        var mvo = CreateTestMvo();
        var importRule = CreateImportSyncRule(); // Wrong direction!

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, importRule);

        // Assert
        Assert.That(result, Is.False, "Should return false when called with non-export rule");
    }

    [Test]
    public void IsMvoInScopeForExportRule_EmptyGroup_ReturnsTrue()
    {
        // Arrange
        var mvo = CreateTestMvo();
        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        // No criteria in the group
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        // Act
        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "Empty group should always return true");
    }

    #region Relative date scoping tests

    private static readonly DateTime FixedNow = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static (MetaverseObject mvo, SyncRule rule, MetaverseAttribute attr) BuildRelativeDateScenario(DateTime accountExpiry)
    {
        var attr = new MetaverseAttribute { Id = 10, Name = "AccountExpiry", Type = AttributeDataType.DateTime };
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = 10, Attribute = attr, DateTimeValue = accountExpiry });
        var rule = CreateExportSyncRule();
        return (mvo, rule, attr);
    }

    [Test]
    public void IsMvoInScopeForExportRule_RelativeOnOrBeforeDaysFromNow_InScope()
    {
        // "on or before 7 days from now" -> boundary 2026-06-22; value 2026-06-18 is before that.
        var (mvo, rule, attr) = BuildRelativeDateScenario(new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc));
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = attr,
            ComparisonType = SearchComparisonType.LessThanOrEquals,
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 7,
            RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.FromNow
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, FixedNow);

        Assert.That(result, Is.True, "Account expiring in 3 days is on or before 7 days from now");
    }

    [Test]
    public void IsMvoInScopeForExportRule_RelativeAfterDaysAgo_OutOfScopeWhenOlder()
    {
        // "after 30 days ago" -> boundary 2026-05-16; value 2026-04-01 is not after it.
        var (mvo, rule, attr) = BuildRelativeDateScenario(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = attr,
            ComparisonType = SearchComparisonType.GreaterThan,
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 30,
            RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.Ago
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, FixedNow);

        Assert.That(result, Is.False, "Account that expired 75 days ago is not after the 30-days-ago boundary");
    }

    [Test]
    public void IsMvoInScopeForExportRule_RelativeCriterion_MissingAttributeValue_OutOfScope()
    {
        var attr = new MetaverseAttribute { Id = 10, Name = "AccountExpiry", Type = AttributeDataType.DateTime };
        var mvo = CreateTestMvo(); // no AccountExpiry value
        var rule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = attr,
            ComparisonType = SearchComparisonType.LessThanOrEquals,
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 7,
            RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.FromNow
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var result = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, FixedNow);

        Assert.That(result, Is.False, "A relative date criterion must not match an object with no value for the attribute");
    }

    [Test]
    public void IsMvoInScopeForExportRule_RelativeBoundaryShiftsAsNowAdvances()
    {
        // Same object and criterion, evaluated at two different "now" values, gives different results.
        // Criterion: AccountExpiry on or before 0 days from now (today, midnight UTC). Value: 2026-06-15 00:00.
        var (mvo, rule, attr) = BuildRelativeDateScenario(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = attr,
            ComparisonType = SearchComparisonType.LessThanOrEquals,
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 0,
            RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.FromNow
        });
        rule.ObjectScopingCriteriaGroups.Add(group);

        var earlier = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc));
        var onTheDay = _scopingEvaluation.IsMvoInScopeForExportRule(mvo, rule, FixedNow);

        Assert.That(earlier, Is.False, "On 10 June, an account expiring 15 June is not yet on or before today");
        Assert.That(onTheDay, Is.True, "On 15 June, an account expiring 15 June is on or before today");
    }

    #endregion

    [Test]
    public void IsCsoInScopeForImportRule_NestedGroups_EvaluatesCorrectly()
    {
        // Arrange
        var ouAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "ou", Type = AttributeDataType.Text };
        var deptAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "department", Type = AttributeDataType.Text };

        var cso = CreateTestCso();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = ouAttr,
            StringValue = "Finance"
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 2,
            Attribute = deptAttr,
            StringValue = "Accounting"
        });

        var importRule = CreateImportSyncRule();

        // Parent group: ANY (OR logic)
        var parentGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.Any };

        // Child group 1: ALL - ou=IT (won't match)
        var childGroup1 = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        childGroup1.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "IT"
        });
        parentGroup.ChildGroups.Add(childGroup1);

        // Child group 2: ALL - ou=Finance AND department=Accounting (will match)
        var childGroup2 = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        childGroup2.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = ouAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });
        childGroup2.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = deptAttr,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Accounting"
        });
        parentGroup.ChildGroups.Add(childGroup2);

        importRule.ObjectScopingCriteriaGroups.Add(parentGroup);

        // Act
        var result = _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);

        // Assert
        Assert.That(result, Is.True, "CSO should be in scope when nested group evaluates to true");
    }

    #endregion

    #region Decimal scoping tests (#1046)

    /// <summary>
    /// Evaluates a single Decimal scoping criterion against an MVO whose attribute holds
    /// <paramref name="mvoValue"/> (or no value at all when null).
    /// </summary>
    private bool EvaluateMvoDecimalCriterion(decimal? mvoValue, decimal? criterionValue, SearchComparisonType comparisonType)
    {
        var salaryAttr = new MetaverseAttribute { Id = 20, Name = "Salary", Type = AttributeDataType.Decimal };
        var mvo = CreateTestMvo();
        if (mvoValue.HasValue)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                AttributeId = 20,
                Attribute = salaryAttr,
                DecimalValue = mvoValue.Value
            });
        }

        var exportRule = CreateExportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            MetaverseAttribute = salaryAttr,
            ComparisonType = comparisonType,
            DecimalValue = criterionValue
        });
        exportRule.ObjectScopingCriteriaGroups.Add(group);

        return _scopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);
    }

    /// <summary>
    /// Evaluates a single Decimal scoping criterion against a CSO whose attribute holds
    /// <paramref name="csoValue"/> (or no value at all when null).
    /// </summary>
    private bool EvaluateCsoDecimalCriterion(decimal? csoValue, decimal? criterionValue, SearchComparisonType comparisonType)
    {
        var salaryAttr = new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "salary", Type = AttributeDataType.Decimal };
        var cso = CreateTestCso();
        if (csoValue.HasValue)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = 20,
                Attribute = salaryAttr,
                DecimalValue = csoValue.Value
            });
        }

        var importRule = CreateImportSyncRule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = salaryAttr,
            ComparisonType = comparisonType,
            DecimalValue = criterionValue
        });
        importRule.ObjectScopingCriteriaGroups.Add(group);

        return _scopingEvaluation.IsCsoInScopeForImportRule(cso, importRule);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.5m, 5.5m, SearchComparisonType.Equals), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.5m, 5.6m, SearchComparisonType.Equals), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalEquals_ScaleDiffersOnly_ReturnsTrue()
    {
        // 5.00 on the object and 5.0 on the criterion are numerically equal; scale must not matter.
        Assert.That(EvaluateMvoDecimalCriterion(5.00m, 5.0m, SearchComparisonType.Equals), Is.True);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalNotEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.5m, 5.6m, SearchComparisonType.NotEquals), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.NotEquals), Is.False,
            "A scale-only difference is numeric equality, so NotEquals must be false");
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalLessThan_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.4m, 5.5m, SearchComparisonType.LessThan), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.5m, 5.5m, SearchComparisonType.LessThan), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalLessThanOrEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.LessThanOrEquals), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.6m, 5.5m, SearchComparisonType.LessThanOrEquals), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalGreaterThan_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.6m, 5.5m, SearchComparisonType.GreaterThan), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.5m, 5.5m, SearchComparisonType.GreaterThan), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalGreaterThanOrEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateMvoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.GreaterThanOrEquals), Is.True);
        Assert.That(EvaluateMvoDecimalCriterion(5.4m, 5.5m, SearchComparisonType.GreaterThanOrEquals), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalMissingValue_NullCriterionEquals_ReturnsTrue()
    {
        // An Equals criterion with no value matches an object with no value for the attribute.
        Assert.That(EvaluateMvoDecimalCriterion(null, null, SearchComparisonType.Equals), Is.True);
    }

    [Test]
    public void IsMvoInScopeForExportRule_DecimalMissingValue_PopulatedCriterion_ReturnsFalse()
    {
        // The all-null check must include DecimalValue: a populated decimal criterion must NOT match
        // an object with no value (omitting the check would silently mis-scope).
        Assert.That(EvaluateMvoDecimalCriterion(null, 5.5m, SearchComparisonType.Equals), Is.False);
    }

    [Test]
    public void IsCsoInScopeForImportRule_DecimalEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateCsoDecimalCriterion(5.5m, 5.5m, SearchComparisonType.Equals), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.00m, 5.0m, SearchComparisonType.Equals), Is.True,
            "A scale-only difference is numeric equality");
        Assert.That(EvaluateCsoDecimalCriterion(5.5m, 5.6m, SearchComparisonType.Equals), Is.False);
    }

    [Test]
    public void IsCsoInScopeForImportRule_DecimalNotEquals_EvaluatesNumerically()
    {
        Assert.That(EvaluateCsoDecimalCriterion(5.5m, 5.6m, SearchComparisonType.NotEquals), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.NotEquals), Is.False);
    }

    [Test]
    public void IsCsoInScopeForImportRule_DecimalRangeOperators_EvaluateNumerically()
    {
        Assert.That(EvaluateCsoDecimalCriterion(5.4m, 5.5m, SearchComparisonType.LessThan), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.LessThanOrEquals), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.6m, 5.5m, SearchComparisonType.GreaterThan), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.50m, 5.5m, SearchComparisonType.GreaterThanOrEquals), Is.True);
        Assert.That(EvaluateCsoDecimalCriterion(5.5m, 5.4m, SearchComparisonType.LessThan), Is.False);
        Assert.That(EvaluateCsoDecimalCriterion(5.4m, 5.5m, SearchComparisonType.GreaterThan), Is.False);
    }

    [Test]
    public void IsCsoInScopeForImportRule_DecimalMissingValue_NullCriterionEquals_ReturnsTrue()
    {
        Assert.That(EvaluateCsoDecimalCriterion(null, null, SearchComparisonType.Equals), Is.True);
    }

    [Test]
    public void IsCsoInScopeForImportRule_DecimalMissingValue_PopulatedCriterion_ReturnsFalse()
    {
        Assert.That(EvaluateCsoDecimalCriterion(null, 5.5m, SearchComparisonType.Equals), Is.False);
    }

    [Test]
    public void IsMvoInScopeForExportRule_TextOperatorOnDecimalAttribute_ThrowsInvalidOperationException()
    {
        // A text operator cannot apply to a Decimal attribute; the evaluator must fail loudly.
        Assert.Throws<InvalidOperationException>(() =>
            EvaluateMvoDecimalCriterion(5.5m, 5.5m, SearchComparisonType.StartsWith));
    }

    #endregion

    #region Helper Methods

    private static MetaverseObject CreateTestMvo()
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "Person" }
        };
    }

    private static ConnectedSystemObject CreateTestCso()
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = new ConnectedSystemObjectType { Id = 1, Name = "user" }
        };
    }

    private static SyncRule CreateExportSyncRule()
    {
        return new SyncRule
        {
            Id = 1,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" }
        };
    }

    private static SyncRule CreateImportSyncRule()
    {
        return new SyncRule
        {
            Id = 2,
            Name = "Test Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" }
        };
    }

    #endregion
}
