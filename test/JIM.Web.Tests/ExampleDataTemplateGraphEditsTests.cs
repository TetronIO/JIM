// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pins the edits the portal's Data Generation Template editor applies to a freshly loaded template before saving it
/// (#1466): rows are found by id so an edit made against a template that has since been saved elsewhere fails rather
/// than landing on the wrong row, a replaced attribute keeps its place, and the last Object Type cannot be removed.
/// </summary>
[TestFixture]
public class ExampleDataTemplateGraphEditsTests
{
    private static readonly MetaverseObjectType UserType = new() { Id = 1, Name = "User", PluralName = "Users" };
    private static readonly MetaverseObjectType GroupType = new() { Id = 2, Name = "Group", PluralName = "Groups" };
    private static readonly MetaverseAttribute FirstName = new() { Id = 11, Name = "First Name", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute EmployeeType = new() { Id = 12, Name = "Employee Type", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute JobTitle = new() { Id = 13, Name = "Job Title", Type = AttributeDataType.Text };

    private ExampleDataTemplate _template = null!;

    [SetUp]
    public void SetUp()
    {
        _template = new ExampleDataTemplate { Id = 7, Name = "Demo" };
        var users = new ExampleDataObjectType { Id = 31, MetaverseObjectType = UserType, ObjectsToCreate = 100 };
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute { Id = 41, MetaverseAttribute = FirstName, Pattern = "a" });
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute { Id = 42, MetaverseAttribute = EmployeeType, Pattern = "b" });
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 43,
            MetaverseAttribute = JobTitle,
            Pattern = "c",
            AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                MetaverseAttribute = EmployeeType, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee"
            }
        });
        _template.ObjectTypes.Add(users);
    }

    [Test]
    public void SaveAttribute_ExistingAttribute_ReplacesItInPlace()
    {
        var replacement = new ExampleDataTemplateAttribute { MetaverseAttribute = EmployeeType, Pattern = "changed" };

        ExampleDataTemplateGraphEdits.SaveAttribute(_template, 31, 42, replacement);

        var attributes = _template.ObjectTypes[0].TemplateAttributes;
        Assert.That(attributes.Select(a => a.Pattern), Is.EqualTo(new[] { "a", "changed", "c" }));
    }

    [Test]
    public void SaveAttribute_NewAttribute_IsAppended()
    {
        ExampleDataTemplateGraphEdits.SaveAttribute(_template, 31, null, new ExampleDataTemplateAttribute { MetaverseAttribute = FirstName, Pattern = "d" });

        Assert.That(_template.ObjectTypes[0].TemplateAttributes, Has.Count.EqualTo(4));
    }

    [Test]
    public void SaveAttribute_RowNoLongerExists_Throws()
    {
        // the template was saved elsewhere since the page loaded, so its rows have new ids.
        Assert.That(() => ExampleDataTemplateGraphEdits.SaveAttribute(_template, 31, 999, new ExampleDataTemplateAttribute()),
            Throws.TypeOf<ExampleDataTemplateChangedException>());
        Assert.That(() => ExampleDataTemplateGraphEdits.SaveAttribute(_template, 999, null, new ExampleDataTemplateAttribute()),
            Throws.TypeOf<ExampleDataTemplateChangedException>());
    }

    [Test]
    public void RemoveAttribute_RemovesOnlyThatRow()
    {
        ExampleDataTemplateGraphEdits.RemoveAttribute(_template, 31, 41);

        Assert.That(_template.ObjectTypes[0].TemplateAttributes.Select(a => a.Id), Is.EqualTo(new[] { 42, 43 }));
    }

    [Test]
    public void AttributesReading_NamesAttributesWhoseConditionOrPatternReadsIt()
    {
        var objectType = _template.ObjectTypes[0];
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 44, MetaverseAttribute = new MetaverseAttribute { Id = 14, Name = "Display Name", Type = AttributeDataType.Text }, Pattern = "{First Name} Example"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExampleDataTemplateGraphEdits.AttributesReading(objectType, EmployeeType), Is.EqualTo(new[] { "Job Title" }));
            Assert.That(ExampleDataTemplateGraphEdits.AttributesReading(objectType, FirstName), Is.EqualTo(new[] { "Display Name" }));
            Assert.That(ExampleDataTemplateGraphEdits.AttributesReading(objectType, JobTitle), Is.Empty);
        }
    }

    [Test]
    public void AddObjectType_AddsItWithItsCount()
    {
        ExampleDataTemplateGraphEdits.AddObjectType(_template, GroupType, 20);

        Assert.That(_template.ObjectTypes.Select(ot => (ot.MetaverseObjectType, ot.ObjectsToCreate)), Is.EqualTo(new[] { (UserType, 100), (GroupType, 20) }));
    }

    [Test]
    public void SetObjectsToCreate_ChangesOnlyThatObjectType()
    {
        ExampleDataTemplateGraphEdits.SetObjectsToCreate(_template, 31, 2500);

        Assert.That(_template.ObjectTypes[0].ObjectsToCreate, Is.EqualTo(2500));
    }

    [Test]
    public void RemoveObjectType_LastOne_Throws()
    {
        // a template with no Object Types cannot be saved, so the editor must refuse before the server does.
        Assert.That(() => ExampleDataTemplateGraphEdits.RemoveObjectType(_template, 31), Throws.InvalidOperationException);
    }

    [Test]
    public void RemoveObjectType_OneOfSeveral_RemovesIt()
    {
        ExampleDataTemplateGraphEdits.AddObjectType(_template, GroupType, 20);

        ExampleDataTemplateGraphEdits.RemoveObjectType(_template, 31);

        Assert.That(_template.ObjectTypes.Single().MetaverseObjectType, Is.SameAs(GroupType));
    }
}
