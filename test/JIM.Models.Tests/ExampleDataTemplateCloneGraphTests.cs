// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JIM.Models.Tests;

/// <summary>
/// Pins <see cref="ExampleDataTemplate.CloneGraph"/> (#1466): the copy owns brand-new template rows (every id zero,
/// no instance shared with the source) while still pointing at the same persisted Metaverse Object Types, Metaverse
/// Attributes and Example Data Sets, so persisting it inserts a new template without touching what it references.
/// </summary>
[TestFixture]
public class ExampleDataTemplateCloneGraphTests
{
    private MetaverseObjectType _userType = null!;
    private MetaverseObjectType _groupType = null!;
    private MetaverseAttribute _firstName = null!;
    private MetaverseAttribute _employeeType = null!;
    private MetaverseAttribute _members = null!;
    private ExampleDataSet _firstNames = null!;
    private ExampleDataTemplate _source = null!;

    [SetUp]
    public void SetUp()
    {
        _userType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" };
        _groupType = new MetaverseObjectType { Id = 2, Name = "Group", PluralName = "Groups" };
        _firstName = new MetaverseAttribute { Id = 11, Name = "First Name", Type = AttributeDataType.Text };
        _employeeType = new MetaverseAttribute { Id = 12, Name = "Employee Type", Type = AttributeDataType.Text };
        _members = new MetaverseAttribute { Id = 13, Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        _firstNames = new ExampleDataSet { Id = 21, Name = "Firstnames", Culture = "en-GB" };

        _source = new ExampleDataTemplate
        {
            Id = 7,
            Name = "Users & Groups",
            BuiltIn = true,
            Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedByType = ActivityInitiatorType.System,
            CreatedByName = "System",
            LastUpdated = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            LastUpdatedByType = ActivityInitiatorType.User,
            LastUpdatedById = Guid.NewGuid(),
            LastUpdatedByName = "Admin"
        };

        var users = new ExampleDataObjectType { Id = 31, MetaverseObjectType = _userType, ObjectsToCreate = 2000 };
        var firstName = new ExampleDataTemplateAttribute
        {
            Id = 41,
            MetaverseAttribute = _firstName,
            PopulatedValuesPercentage = 90,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Id = 51, Value = "Ada", Weight = 0.5f }],
            AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                Id = 61, MetaverseAttribute = _employeeType, ComparisonType = SearchComparisonType.Equals, StringValue = "Employee"
            }
        };
        firstName.ExampleDataSetInstances.Add(new ExampleDataSetInstance { Id = 71, ExampleDataSet = _firstNames, ExampleDataTemplateAttribute = firstName, Order = 0 });
        users.TemplateAttributes.Add(firstName);

        var groups = new ExampleDataObjectType { Id = 32, MetaverseObjectType = _groupType, ObjectsToCreate = 50 };
        groups.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 42,
            MetaverseAttribute = _members,
            MvaRefMinAssignments = 1,
            MvaRefMaxAssignments = 10,
            ReferenceMetaverseObjectTypes = [_userType]
        });

        _source.ObjectTypes.Add(users);
        _source.ObjectTypes.Add(groups);
    }

    [Test]
    public void CloneGraph_EveryTemplateOwnedNode_IsNewWithIdZero()
    {
        var clone = _source.CloneGraph();

        Assert.That(clone, Is.Not.SameAs(_source));
        Assert.That(clone.Id, Is.Zero);
        Assert.That(clone.ObjectTypes, Has.Count.EqualTo(2));

        var sourceAttributes = _source.ObjectTypes.SelectMany(ot => ot.TemplateAttributes).ToList();
        foreach (var objectType in clone.ObjectTypes)
        {
            Assert.That(objectType.Id, Is.Zero);
            Assert.That(_source.ObjectTypes, Has.None.SameAs(objectType));
            foreach (var attribute in objectType.TemplateAttributes)
            {
                Assert.That(attribute.Id, Is.Zero);
                Assert.That(sourceAttributes, Has.None.SameAs(attribute));
            }
        }

        var cloneFirstName = clone.ObjectTypes[0].TemplateAttributes[0];
        Assert.That(cloneFirstName.WeightedStringValues!.Single().Id, Is.Zero);
        Assert.That(cloneFirstName.WeightedStringValues, Is.Not.SameAs(sourceAttributes[0].WeightedStringValues));
        Assert.That(cloneFirstName.AttributeDependency!.Id, Is.Zero);
        Assert.That(cloneFirstName.AttributeDependency, Is.Not.SameAs(sourceAttributes[0].AttributeDependency));

        var instance = cloneFirstName.ExampleDataSetInstances.Single();
        Assert.That(instance.Id, Is.Zero);
        Assert.That(instance.ExampleDataTemplateAttribute, Is.SameAs(cloneFirstName),
            "the copied Example Data Set instance must point back at the copied attribute, not the source's");
    }

    [Test]
    public void CloneGraph_PersistedReferences_AreSharedNotCopied()
    {
        var clone = _source.CloneGraph();

        Assert.That(clone.ObjectTypes[0].MetaverseObjectType, Is.SameAs(_userType));
        Assert.That(clone.ObjectTypes[1].MetaverseObjectType, Is.SameAs(_groupType));

        var firstName = clone.ObjectTypes[0].TemplateAttributes[0];
        Assert.That(firstName.MetaverseAttribute, Is.SameAs(_firstName));
        Assert.That(firstName.AttributeDependency!.MetaverseAttribute, Is.SameAs(_employeeType));
        Assert.That(firstName.ExampleDataSetInstances.Single().ExampleDataSet, Is.SameAs(_firstNames));

        var members = clone.ObjectTypes[1].TemplateAttributes[0];
        Assert.That(members.ReferenceMetaverseObjectTypes!.Single(), Is.SameAs(_userType));
        Assert.That(members.ReferenceMetaverseObjectTypes, Is.Not.SameAs(_source.ObjectTypes[1].TemplateAttributes[0].ReferenceMetaverseObjectTypes));
    }

    [Test]
    public void CloneGraph_GenerationRules_AreCopied()
    {
        var clone = _source.CloneGraph();

        Assert.That(clone.Name, Is.EqualTo(_source.Name));
        Assert.That(clone.ObjectTypes.Select(ot => ot.ObjectsToCreate), Is.EqualTo(new[] { 2000, 50 }));

        var firstName = clone.ObjectTypes[0].TemplateAttributes[0];
        Assert.That(firstName.PopulatedValuesPercentage, Is.EqualTo(90));
        Assert.That(firstName.WeightedStringValues!.Single().Value, Is.EqualTo("Ada"));
        Assert.That(firstName.WeightedStringValues!.Single().Weight, Is.EqualTo(0.5f));
        Assert.That(firstName.AttributeDependency!.ComparisonType, Is.EqualTo(SearchComparisonType.Equals));
        Assert.That(firstName.AttributeDependency!.StringValue, Is.EqualTo("Employee"));

        var members = clone.ObjectTypes[1].TemplateAttributes[0];
        Assert.That(members.MvaRefMinAssignments, Is.EqualTo(1));
        Assert.That(members.MvaRefMaxAssignments, Is.EqualTo(10));
    }

    [Test]
    public void CloneGraph_BuiltInAndAuditProvenance_AreNotCarriedOver()
    {
        var clone = _source.CloneGraph();

        Assert.That(clone.BuiltIn, Is.False, "a copy of a built-in template is the administrator's own");
        Assert.That(clone.CreatedByType, Is.EqualTo(ActivityInitiatorType.NotSet));
        Assert.That(clone.CreatedByName, Is.Null);
        Assert.That(clone.LastUpdated, Is.Null);
        Assert.That(clone.LastUpdatedById, Is.Null);
        Assert.That(clone.LastUpdatedByName, Is.Null);
    }

    [Test]
    public void CloneGraph_EveryScalarGenerationRule_IsCopied()
    {
        // a reflection guard: a scalar property added to ExampleDataTemplateAttribute later must be copied too, or a
        // duplicated template would silently generate different data from its source.
        var source = new ExampleDataTemplateAttribute
        {
            Id = 1,
            MetaverseAttribute = _firstName,
            PopulatedValuesPercentage = 50,
            BoolTrueDistribution = 30,
            MinDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MaxDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MinNumber = 1,
            MaxNumber = 9,
            SequentialNumbers = true,
            RandomNumbers = false,
            Pattern = "{0}",
            Expression = "\"x\"",
            ManagerDepthPercentage = 40,
            MvaRefMinAssignments = 2,
            MvaRefMaxAssignments = 3
        };
        var template = new ExampleDataTemplate { Name = "T" };
        template.ObjectTypes.Add(new ExampleDataObjectType { MetaverseObjectType = _userType });
        template.ObjectTypes[0].TemplateAttributes.Add(source);

        var copy = template.CloneGraph().ObjectTypes[0].TemplateAttributes[0];

        var scalarProperties = typeof(ExampleDataTemplateAttribute).GetProperties()
            .Where(p => p.CanWrite && p.Name != nameof(ExampleDataTemplateAttribute.Id) &&
                        (p.PropertyType.IsValueType || p.PropertyType == typeof(string)));
        foreach (var property in scalarProperties)
            Assert.That(property.GetValue(copy), Is.EqualTo(property.GetValue(source)), property.Name);
    }
}
