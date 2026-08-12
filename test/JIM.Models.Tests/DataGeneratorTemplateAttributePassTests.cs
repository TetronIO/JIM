// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Staging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
namespace JIM.Models.Tests;

public class DataGeneratorTemplateAttributePassTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestIsValidMvAttributePass()
    {
        var subject = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };

        Assert.That(subject.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidExpressionPass()
    {
        var subject = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            Expression = "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"]) + \"@example.io\""
        };

        Assert.That(subject.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidCsAttributePass()
    {
        var subject = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.That(subject.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidPopulatedValuesPercentagePass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 1,
            Pattern = "dummy-value"
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 50,
            Pattern = "dummy-value"
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.That(subject3.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidNumberTypeSequentialPass()
    {
        // numbers can be assigned to attributes of type number AND string
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            MinNumber = 1
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            MaxNumber = 50
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            MaxNumber = 100
        };
        Assert.That(subject3.Validate, Throws.Nothing);

        var subject4 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true
        };
        Assert.That(subject4.Validate, Throws.Nothing);

        var subject5 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            MinNumber = 1
        };
        Assert.That(subject5.Validate, Throws.Nothing);

        var subject6 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            MinNumber = 1
        };
        Assert.That(subject6.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidNumberTypeRandomPass()
    {
        // numbers can be assigned to attributes of type number AND string
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MinNumber = 1
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MaxNumber = 50
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MinNumber = 0,
            MaxNumber = 100
        };
        Assert.That(subject3.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidNumberTypeOnStringPass()
    {
        // numbers can be assigned to attributes of type number AND string
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MinNumber = 1
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MaxNumber = 50
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true,
            MinNumber = 0,
            MaxNumber = 100
        };
        Assert.That(subject3.Validate, Throws.Nothing);

        var subject4 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            MinNumber = 1
        };
        Assert.That(subject4.Validate, Throws.Nothing);

        var subject5 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            MaxNumber = 50
        };
        Assert.That(subject5.Validate, Throws.Nothing);

        var subject6 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            MinNumber = 0,
            MaxNumber = 100
        };
        Assert.That(subject6.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidBoolPass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = true
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = false
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolTrueDistribution = 1
        };
        Assert.That(subject3.Validate, Throws.Nothing);

        var subject4 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolTrueDistribution = 50
        };
        Assert.That(subject4.Validate, Throws.Nothing);

        var subject5 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolTrueDistribution = 100
        };
        Assert.That(subject5.Validate, Throws.Nothing);

        var subject6 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            BoolTrueDistribution = 100,
            BoolShouldBeRandom = true
        };
        Assert.That(subject6.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidDateTimePass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            MaxDate = DateTime.UtcNow
        };
        Assert.That(subject3.Validate, Throws.Nothing);

        var subject4 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow,
            MaxDate = DateTime.UtcNow.AddDays(10)
        };
        Assert.That(subject4.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidStringPass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new() }
        };
        Assert.That(subject2.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidWeightedStringValuesPass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            WeightedStringValues = new List<ExampleDataTemplateAttributeWeightedValue>
            {
                new() { Value = "Active", Weight = 0.85f },
                new() { Value = "Suspended", Weight = 0.1f },
                new() { Value = "Leaver", Weight = 0.05f }
            },
            PopulatedValuesPercentage = 100
        };

        Assert.That(subject1.Validate, Throws.Nothing);
    }

    [Test]
    public void TestExampleDataSetUsagePass()
    {
        // you can assign one or more ExampleDataSets with no pattern
        // you can assign one or more ExampleDAtaSets with a pattern

        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new() }
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new(), new() }
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new(), new() },
            Pattern = "{0} {1}"
        };
        Assert.That(subject3.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidGuidPass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Guid },
            PopulatedValuesPercentage = 100
        };
        Assert.That(subject1.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidReferencePass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.StaticMembers },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100
        };
        Assert.That(subject1.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidMvaReferencePass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 10
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMaxAssignments = 10
        };
        Assert.That(subject2.Validate, Throws.Nothing);

        var subject3 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 10,
            MvaRefMaxAssignments = 100
        };
        Assert.That(subject3.Validate, Throws.Nothing);
    }

    [Test]
    public void TestIsValidManagerPass()
    {
        var subject1 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 50
        };
        Assert.That(subject1.Validate, Throws.Nothing);

        var subject2 = new ExampleDataTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 95
        };
        Assert.That(subject2.Validate, Throws.Nothing);
    }
}