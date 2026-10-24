// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Object Changes detail panel (#2460): what it offers for a change depends on whether the object still exists,
/// whether the change is the deletion itself, and whether the change was made in an Activity.
/// </summary>
[TestFixture]
public class ObjectChangeDetailTests : JimComponentTestContext
{
    [Test]
    public void ObjectChangeDetail_LiveObject_OffersToOpenItAndListsEveryAttributeChange()
    {
        var result = Result(ObjectChangeType.AttributeFlow, deleted: false,
            Attribute("Department", (ValueChangeType.Remove, "Risk"), (ValueChangeType.Add, "Compliance")),
            Attribute("Job Title", (ValueChangeType.Add, "Senior Analyst")));

        var cut = Render<ObjectChangeDetail>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=open-object]"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll("[data-testid=attribute-row]"), Has.Count.EqualTo(2), "the panel shows every change, not a summary");
            Assert.That(cut.FindComponent<ObjectChip>().Instance.Deleted, Is.False);
        }
    }

    [Test]
    public void ObjectChangeDetail_DeletedObject_HasNothingToOpenAndMarksTheChipDeleted()
    {
        var result = Result(ObjectChangeType.AttributeFlow, deleted: true, Attribute("Department", (ValueChangeType.Add, "Finance")));

        var cut = Render<ObjectChangeDetail>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=open-object]"), Is.Empty);
            Assert.That(cut.FindComponent<ObjectChip>().Instance.Deleted, Is.True);
        }
    }

    [Test]
    public void ObjectChangeDetail_Deletion_ListsTheFinalValuesNotChanges()
    {
        var result = Result(ObjectChangeType.Deleted, deleted: true,
            Attribute("Account Name", (ValueChangeType.Remove, "tina.adams")));

        var cut = Render<ObjectChangeDetail>(p => p.Add(c => c.Result, result));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=final-values]"), Has.Count.EqualTo(1));
            Assert.That(cut.FindComponent<ChangeValueCell>().Instance.FinalValue, Is.True);
        }
    }

    [Test]
    public void ObjectChangeDetail_MadeInAnActivity_LinksToIt()
    {
        var activityId = Guid.NewGuid();
        var result = Result(ObjectChangeType.Deleted, deleted: true, Attribute("Account Name", (ValueChangeType.Remove, "tina.adams")));
        result.Change.ActivityId = activityId;

        var cut = Render<ObjectChangeDetail>(p => p.Add(c => c.Result, result));

        Assert.That(cut.Find("[data-testid=activity-link]").GetAttribute("href"), Is.EqualTo($"/activity/{activityId}"));
    }

    [Test]
    public void ObjectChangeDetail_NoActivity_HasNoActivityLink()
    {
        var result = Result(ObjectChangeType.AttributeFlow, deleted: false, Attribute("Department", (ValueChangeType.Add, "Finance")));

        var cut = Render<ObjectChangeDetail>(p => p.Add(c => c.Result, result));

        Assert.That(cut.FindAll("[data-testid=activity-link]"), Is.Empty);
    }

    [Test]
    public void ObjectChangeDetail_Close_RaisesOnClose()
    {
        var closed = false;
        var result = Result(ObjectChangeType.AttributeFlow, deleted: false, Attribute("Department", (ValueChangeType.Add, "Finance")));

        var cut = Render<ObjectChangeDetail>(p => p
            .Add(c => c.Result, result)
            .Add(c => c.OnClose, () => closed = true));
        cut.Find("[data-testid=close-detail]").Click();

        Assert.That(closed, Is.True);
    }

    private static MetaverseObjectChangeSearchResult Result(ObjectChangeType changeType, bool deleted, params MvoAttributeChangeDto[] attributes) => new()
    {
        MetaverseObjectId = Guid.NewGuid(),
        MetaverseObjectDeleted = deleted,
        MetaverseObjectDeletedAt = deleted ? DateTime.UtcNow.AddHours(-7) : null,
        MetaverseObjectDisplayName = "Tina Adams",
        MetaverseObjectTypeName = "User",
        MetaverseObjectTypePluralName = "Users",
        Change = new MvoChangeHistoryDto
        {
            Id = Guid.NewGuid(),
            ChangeType = changeType,
            ChangeTime = DateTime.UtcNow.AddHours(-7),
            SyncRuleName = "HR Inbound",
            AttributeChanges = attributes.ToList()
        }
    };

    private static MvoAttributeChangeDto Attribute(string name, params (ValueChangeType Type, string Value)[] values) => new()
    {
        AttributeId = name.GetHashCode() & 0xFFFF,
        AttributeName = name,
        AttributeType = JIM.Models.Core.AttributeDataType.Text,
        AttributePlurality = JIM.Models.Core.AttributePlurality.SingleValued,
        ValueChanges = values.Select(v => new MvoValueChangeDto { ValueChangeType = v.Type, StringValue = v.Value }).ToList()
    };
}
