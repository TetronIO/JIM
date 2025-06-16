using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
namespace JIM.Models.Tests;

public class DataGeneratorTemplateAttributeFailTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestIsValidAttributeFail()
    {
        var subject = new DataGenerationTemplateAttribute
        {
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };

        Assert.Catch<DataGenerationTemplateAttributeException>(subject.Validate);
    }

    [Test]
    public void TestIsValidPopulatedValuesPercentageTooLowFail()
    {
        var subject = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 0,
            Pattern = "dummy-value"
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject.Validate);
    }

    [Test]
    public void TestIsValidPopulatedValuesPercentageTooHighFail()
    {
        var subject = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 101,
            Pattern = "dummy-value"
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject.Validate);
    }

    [Test]
    public void TestIsValidNumberTypeFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            MinNumber = 100,
            MaxNumber = 50
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true,
            RandomNumbers = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);
    }

    [Test]
    public void TestIsValidDateTimeFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow.AddDays(1),
            MaxDate = DateTime.UtcNow
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow,
            MaxDate = DateTime.UtcNow.AddDays(-1)
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);
    }

    [Test]
    public void TestIsValidNumberMismatchFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow                
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);

        var subject3 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new() }

        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject3.Validate);

        var subject4 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject4.Validate);
    }

    [Test]
    public void TestIsValidBoolMismatchFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);

        var subject3 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance() }
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject3.Validate);

        var subject4 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject4.Validate);

        var subject5 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Boolean },
            PopulatedValuesPercentage = 100,
            SequentialNumbers = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject5.Validate);
    }

    [Test]
    public void TestIsValidDateTimeMismatchFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            Pattern = "dummy-value"
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);

        var subject3 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance() }
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject3.Validate);

        var subject4 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.DateTime },
            PopulatedValuesPercentage = 100,
            RandomNumbers = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject4.Validate);
    }

    [Test]
    public void TestIsValidStringMismatchFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = true
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
            PopulatedValuesPercentage = 100,
            MinDate = DateTime.UtcNow
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);
    }

    [Test]
    public void TestIsValidWeightedStringValuesFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
            WeightedStringValues = new List<DataGenerationTemplateAttributeWeightedValue>
            {
                new() { Value = "Active", Weight = 0.85f },
                new() { Value = "Suspended", Weight = 0.1f },
                new() { Value = "Leaver", Weight = 0.05f }
            },
            PopulatedValuesPercentage = 100,
            BoolShouldBeRandom = true
        };

        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);
    }

    [Test]
    public void TestIsValidManagerFail()
    {
        // cannot use PopulatedValuesPercentage and ManagerDepthPercentage together
        var subject1 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            PopulatedValuesPercentage = 100,
            ManagerDepthPercentage = 50
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        // ManagerDepthPercentage cannot be zero. If you don't want to use ManagerDepthPercentage, then set it to null
        var subject2 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 0
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);

        // not everyone can be a manager
        var subject3 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 100
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject3.Validate);

        // outside of bounds
        var subject4 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 120
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject4.Validate);

        // outside of bounds
        var subject5 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = -10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject5.Validate);

        // ManagerDepthPercentage can only be used on reference attribute types
        var subject6 = new DataGenerationTemplateAttribute
        {
            ConnectedSystemObjectTypeAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text, Name = Constants.BuiltInAttributes.Manager },
            ManagerDepthPercentage = 50
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject6.Validate);
    }

    [Test]
    public void TestIsValidMvaReferenceFail()
    {
        var subject1 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Reference },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = -1
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject1.Validate);

        // min must be less than max
        var subject2 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Reference },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 100,
            MvaRefMaxAssignments = 10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject2.Validate);

        // min must be less than max
        var subject3 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Reference },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 10,
            MvaRefMaxAssignments = 10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject3.Validate);

        // can't use MvaRefMinAssignments or MvaRefMaxAssignments on non-mva reference attributes
        var subject4 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 0,
            MvaRefMaxAssignments = 10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject4.Validate);

        var subject5 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
            ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new() },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 0,
            MvaRefMaxAssignments = 10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject5.Validate);

        // ReferenceMetaverseObjectTypes are needed
        var subject6 = new DataGenerationTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
            PopulatedValuesPercentage = 100,
            MvaRefMinAssignments = 0,
            MvaRefMaxAssignments = 10
        };
        Assert.Catch<DataGenerationTemplateAttributeException>(subject6.Validate);
    }
}