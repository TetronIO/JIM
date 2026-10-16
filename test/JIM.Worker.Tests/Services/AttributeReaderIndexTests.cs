// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The configuration that reads an attribute (#2265): Scoping Criteria comparing it or reading it in an Expression,
/// and Expressions in Attribute Flows, Object Matching Rules and Example Data Templates. Names match ignoring case and
/// only within the side and Object Type the reader evaluates against.
/// </summary>
[TestFixture]
public class AttributeReaderIndexTests
{
    private const int DepartmentId = 10;
    private const int PersonTypeId = 1;
    private const int GroupTypeId = 2;
    private const int UserCsTypeId = 7;
    private const int OtherCsTypeId = 8;

    [Test]
    public void ForMetaverseAttribute_ScopingCriterionComparingIt_IsAReaderNamingTheRule()
    {
        var index = new AttributeReaderIndex([Criterion(itemId: 5, metaverseAttributeId: DepartmentId)]);

        var reader = index.ForMetaverseAttribute(DepartmentId, "Department").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.Kind, Is.EqualTo(AttributeReaderKind.ScopingCriterion));
            Assert.That(reader.ItemId, Is.EqualTo(5));
            Assert.That(reader.ViaExpression, Is.False);
            Assert.That(reader.SyncRuleName, Is.EqualTo("Export users"));
            Assert.That(reader.Description, Does.Contain("Export users").And.Contain("Department"));
        }
    }

    [Test]
    public void ForMetaverseAttribute_ScopingCriterionExpressionReadingItInAnotherCase_IsAReader()
    {
        var index = new AttributeReaderIndex([Criterion(itemId: 5, expression: "Lower(mv[\"department\"])")]);

        var reader = index.ForMetaverseAttribute(DepartmentId, "Department").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.ViaExpression, Is.True);
            Assert.That(reader.Expression, Is.EqualTo("Lower(mv[\"department\"])"));
        }
    }

    [Test]
    public void ForMetaverseAttribute_ExpressionReadingTheConnectedSystemSideOfTheSameName_IsNotAReader()
    {
        var index = new AttributeReaderIndex([Flow(itemId: 3, expression: "Lower(cs[\"Department\"])")]);

        Assert.That(index.ForMetaverseAttribute(DepartmentId, "Department"), Is.Empty);
    }

    [Test]
    public void ForMetaverseAttribute_EveryExpressionKind_IsAReader()
    {
        var index = new AttributeReaderIndex(
        [
            Flow(itemId: 3, expression: "mv[\"Department\"] + \"x\""),
            new AttributeReaderCandidate
            {
                Kind = AttributeReaderKind.ObjectMatchingRuleExpression, ItemId = 4, SyncRuleId = 20, SyncRuleName = "Export users",
                ConnectedSystemObjectTypeId = UserCsTypeId, MetaverseObjectTypeId = PersonTypeId, Expression = "mv[\"Department\"]"
            },
            new AttributeReaderCandidate
            {
                Kind = AttributeReaderKind.ExampleDataTemplateExpression, ItemId = 6, ExampleDataTemplateId = 9, ExampleDataTemplateName = "Demo",
                MetaverseObjectTypeId = PersonTypeId, Expression = "mv[\"Department\"]", TargetName = "Description"
            }
        ]);

        var readers = index.ForMetaverseAttribute(DepartmentId, "Department");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readers.Select(r => r.Kind), Is.EquivalentTo(new[]
            {
                AttributeReaderKind.AttributeFlowExpression,
                AttributeReaderKind.ObjectMatchingRuleExpression,
                AttributeReaderKind.ExampleDataTemplateExpression
            }));
            Assert.That(readers.Single(r => r.Kind == AttributeReaderKind.ExampleDataTemplateExpression).Description,
                Does.Contain("Demo").And.Contain("Description"));
        }
    }

    [Test]
    public void ForMetaverseAttribute_ScopedToAnObjectType_LeavesReadersOfOtherTypesOut()
    {
        var index = new AttributeReaderIndex(
        [
            Criterion(itemId: 5, metaverseAttributeId: DepartmentId),
            Criterion(itemId: 6, metaverseAttributeId: DepartmentId, metaverseObjectTypeId: GroupTypeId)
        ]);

        var readers = index.ForMetaverseAttribute(DepartmentId, "Department", metaverseObjectTypeId: GroupTypeId);

        Assert.That(readers.Select(r => r.ItemId), Is.EqualTo(new[] { 6 }));
    }

    [Test]
    public void ForMetaverseAttribute_ExpressionsOnly_LeavesADirectComparisonOut()
    {
        var index = new AttributeReaderIndex(
        [
            Criterion(itemId: 5, metaverseAttributeId: DepartmentId),
            Criterion(itemId: 6, expression: "mv[\"Department\"]")
        ]);

        var readers = index.ForMetaverseAttribute(DepartmentId, "Department", expressionsOnly: true);

        Assert.That(readers.Select(r => r.ItemId), Is.EqualTo(new[] { 6 }));
    }

    [Test]
    public void ForConnectedSystemAttribute_ReadersOfThatObjectTypeOnly()
    {
        var index = new AttributeReaderIndex(
        [
            ImportCriterion(itemId: 1, connectedSystemAttributeId: 55, csTypeId: UserCsTypeId),
            ImportCriterion(itemId: 2, expression: "Trim(cs[\"DEPARTMENT\"])", csTypeId: UserCsTypeId),
            ImportCriterion(itemId: 3, expression: "Trim(cs[\"department\"])", csTypeId: OtherCsTypeId),
            ImportCriterion(itemId: 4, expression: "Trim(mv[\"department\"])", csTypeId: UserCsTypeId)
        ]);

        var readers = index.ForConnectedSystemAttribute(55, "department", UserCsTypeId);

        Assert.That(readers.Select(r => r.ItemId), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void ForConnectedSystemAttribute_ObjectMatchingRuleOnTheObjectType_NamesTheObjectType()
    {
        var index = new AttributeReaderIndex(
        [
            new AttributeReaderCandidate
            {
                Kind = AttributeReaderKind.ObjectMatchingRuleExpression, ItemId = 4,
                ConnectedSystemObjectTypeId = UserCsTypeId, ConnectedSystemObjectTypeName = "user", Expression = "cs[\"employeeId\"]"
            }
        ]);

        var reader = index.ForConnectedSystemAttribute(56, "employeeId", UserCsTypeId).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.SyncRuleId, Is.Null);
            Assert.That(reader.Description, Does.Contain("'user'"));
        }
    }

    [Test]
    public void ForMetaverseAttribute_NothingReadsIt_ReturnsNoReaders()
    {
        var index = new AttributeReaderIndex([Criterion(itemId: 5, metaverseAttributeId: 99), Flow(itemId: 3, expression: "mv[\"Title\"]")]);

        Assert.That(index.ForMetaverseAttribute(DepartmentId, "Department"), Is.Empty);
    }

    private static AttributeReaderCandidate Criterion(int itemId, int? metaverseAttributeId = null, string? expression = null, int metaverseObjectTypeId = PersonTypeId) => new()
    {
        Kind = AttributeReaderKind.ScopingCriterion,
        ItemId = itemId,
        SyncRuleId = 20,
        SyncRuleName = "Export users",
        ConnectedSystemObjectTypeId = UserCsTypeId,
        MetaverseObjectTypeId = metaverseObjectTypeId,
        MetaverseAttributeId = metaverseAttributeId,
        AttributeName = metaverseAttributeId.HasValue ? "Department" : null,
        Expression = expression
    };

    private static AttributeReaderCandidate ImportCriterion(int itemId, int csTypeId, int? connectedSystemAttributeId = null, string? expression = null) => new()
    {
        Kind = AttributeReaderKind.ScopingCriterion,
        ItemId = itemId,
        SyncRuleId = 21,
        SyncRuleName = "Import users",
        ConnectedSystemObjectTypeId = csTypeId,
        MetaverseObjectTypeId = PersonTypeId,
        ConnectedSystemAttributeId = connectedSystemAttributeId,
        AttributeName = connectedSystemAttributeId.HasValue ? "department" : null,
        Expression = expression
    };

    private static AttributeReaderCandidate Flow(int itemId, string expression) => new()
    {
        Kind = AttributeReaderKind.AttributeFlowExpression,
        ItemId = itemId,
        SyncRuleId = 20,
        SyncRuleName = "Export users",
        ConnectedSystemObjectTypeId = UserCsTypeId,
        MetaverseObjectTypeId = PersonTypeId,
        Expression = expression,
        TargetName = "description"
    };
}
