// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for Example Data Sets and Data Generation Templates (#1447). The raw template entity
/// reached live schema entities (ExampleDataTemplateAttribute carries a ConnectedSystemObjectTypeAttribute),
/// which is how PR #1446's OpenAPI recursion happened; the DTOs carry ids and names instead.
/// </summary>
[TestFixture]
public class ExampleDataDtoTests
{
    [Test]
    public void ExampleDataSetDto_FromEntity_MapsScalarsAndValues()
    {
        var entity = new ExampleDataSet
        {
            Id = 5,
            Name = "UK Cities",
            Culture = "en-GB",
            BuiltIn = true,
            Created = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc),
            LastUpdated = new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc),
            CreatedByName = "System",
            LastUpdatedByName = "Jay",
            Values =
            [
                new ExampleDataSetValue { Id = 1, StringValue = "London" },
                new ExampleDataSetValue { Id = 2, StringValue = "Manchester" }
            ]
        };

        var dto = ExampleDataSetDto.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(5));
            Assert.That(dto.Name, Is.EqualTo("UK Cities"));
            Assert.That(dto.Culture, Is.EqualTo("en-GB"));
            Assert.That(dto.BuiltIn, Is.True);
            Assert.That(dto.Created, Is.EqualTo(new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc)));
            Assert.That(dto.LastUpdated, Is.EqualTo(new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc)));
            Assert.That(dto.CreatedByName, Is.EqualTo("System"));
            Assert.That(dto.LastUpdatedByName, Is.EqualTo("Jay"));
            Assert.That(dto.Values.Select(v => v.StringValue), Is.EqualTo(new[] { "London", "Manchester" }));
            Assert.That(dto.Values.Select(v => v.Id), Is.EqualTo(new[] { 1, 2 }));
        }
    }

    private static ExampleDataTemplate BuildTemplate()
    {
        var metaverseObjectType = new MetaverseObjectType { Id = 7, Name = "User" };
        var firstName = new MetaverseAttribute { Id = 31, Name = "First Name" };
        var status = new MetaverseAttribute { Id = 32, Name = "Status" };

        var objectType = new ExampleDataObjectType
        {
            Id = 12,
            MetaverseObjectType = metaverseObjectType,
            ObjectsToCreate = 500
        };

        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 41,
            MetaverseAttribute = firstName,
            PopulatedValuesPercentage = 100,
            Pattern = "{0}",
            ExampleDataSetInstances =
            [
                new ExampleDataSetInstance
                {
                    Id = 61,
                    Order = 0,
                    ExampleDataSet = new ExampleDataSet { Id = 5, Name = "Firstnames (Female)", Culture = "en-GB" }
                }
            ]
        });

        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 42,
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Id = 91, Name = "EMPLOYEE_STATUS" },
            WeightedStringValues =
            [
                new ExampleDataTemplateAttributeWeightedValue { Id = 71, Value = "active", Weight = 0.9f },
                new ExampleDataTemplateAttributeWeightedValue { Id = 72, Value = "retired", Weight = 0.1f }
            ],
            AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                Id = 81,
                MetaverseAttribute = status,
                ComparisonType = ComparisonType.Equals,
                StringValue = "Employed"
            },
            ReferenceMetaverseObjectTypes = [metaverseObjectType],
            MvaRefMinAssignments = 1,
            MvaRefMaxAssignments = 5,
            ManagerDepthPercentage = 80
        });

        var template = new ExampleDataTemplate
        {
            Id = 9,
            Name = "Demo Users",
            BuiltIn = false,
            Created = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
            CreatedByName = "Jay"
        };
        template.ObjectTypes.Add(objectType);
        return template;
    }

    [Test]
    public void ExampleDataTemplateDto_FromEntity_MapsTheObjectTypeTree()
    {
        var dto = ExampleDataTemplateDto.FromEntity(BuildTemplate());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Id, Is.EqualTo(9));
            Assert.That(dto.Name, Is.EqualTo("Demo Users"));
            Assert.That(dto.BuiltIn, Is.False);
            Assert.That(dto.CreatedByName, Is.EqualTo("Jay"));
            Assert.That(dto.ObjectTypes, Has.Count.EqualTo(1));
        }

        var objectType = dto.ObjectTypes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Id, Is.EqualTo(12));
            Assert.That(objectType.MetaverseObjectTypeId, Is.EqualTo(7));
            Assert.That(objectType.MetaverseObjectTypeName, Is.EqualTo("User"));
            Assert.That(objectType.ObjectsToCreate, Is.EqualTo(500));
            Assert.That(objectType.TemplateAttributes, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void ExampleDataTemplateDto_FromEntity_MapsAttributeTargetsAsIdsAndNames()
    {
        var dto = ExampleDataTemplateDto.FromEntity(BuildTemplate());
        var attributes = dto.ObjectTypes.Single().TemplateAttributes;

        var metaverseTargeted = attributes.Single(a => a.Id == 41);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metaverseTargeted.MetaverseAttributeId, Is.EqualTo(31));
            Assert.That(metaverseTargeted.MetaverseAttributeName, Is.EqualTo("First Name"));
            Assert.That(metaverseTargeted.ConnectedSystemObjectTypeAttributeId, Is.Null);
            Assert.That(metaverseTargeted.PopulatedValuesPercentage, Is.EqualTo(100));
            Assert.That(metaverseTargeted.Pattern, Is.EqualTo("{0}"));
        }

        var dataSetInstance = metaverseTargeted.ExampleDataSetInstances.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dataSetInstance.ExampleDataSetId, Is.EqualTo(5));
            Assert.That(dataSetInstance.ExampleDataSetName, Is.EqualTo("Firstnames (Female)"));
            Assert.That(dataSetInstance.Order, Is.EqualTo(0));
        }

        var schemaTargeted = attributes.Single(a => a.Id == 42);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemaTargeted.ConnectedSystemObjectTypeAttributeId, Is.EqualTo(91));
            Assert.That(schemaTargeted.ConnectedSystemObjectTypeAttributeName, Is.EqualTo("EMPLOYEE_STATUS"));
            Assert.That(schemaTargeted.MetaverseAttributeId, Is.Null);
            Assert.That(schemaTargeted.ManagerDepthPercentage, Is.EqualTo(80));
            Assert.That(schemaTargeted.MvaRefMinAssignments, Is.EqualTo(1));
            Assert.That(schemaTargeted.MvaRefMaxAssignments, Is.EqualTo(5));
        }

        Assert.That(schemaTargeted.WeightedStringValues!.Select(w => (w.Value, w.Weight)),
            Is.EqualTo(new[] { ("active", 0.9f), ("retired", 0.1f) }));

        var dependency = schemaTargeted.AttributeDependency!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependency.MetaverseAttributeId, Is.EqualTo(32));
            Assert.That(dependency.MetaverseAttributeName, Is.EqualTo("Status"));
            Assert.That(dependency.ComparisonType, Is.EqualTo("Equals"));
            Assert.That(dependency.StringValue, Is.EqualTo("Employed"));
        }

        Assert.That(schemaTargeted.ReferenceMetaverseObjectTypes!.Single().Name, Is.EqualTo("User"));
    }

    [Test]
    public void ExampleDataTemplateDto_ExposesNoLiveSchemaEntities()
    {
        // the whole point of the template DTO: no property anywhere on the DTO tree may be a JIM.Models entity
        // (that is how the OpenAPI generator ended up recursing through ConnectedSystemObjectTypeAttribute on #1446).
        var dtoTypes = new[]
        {
            typeof(ExampleDataTemplateDto),
            typeof(ExampleDataTemplateObjectTypeDto),
            typeof(ExampleDataTemplateAttributeDto)
        };

        var entityTypedProperties = dtoTypes
            .SelectMany(t => t.GetProperties())
            .Where(p =>
            {
                var type = p.PropertyType.IsGenericType ? p.PropertyType.GetGenericArguments()[0] : p.PropertyType;
                return type.Namespace != null && type.Namespace.StartsWith("JIM.Models", StringComparison.Ordinal);
            })
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");

        Assert.That(entityTypedProperties, Is.Empty);
    }
}
