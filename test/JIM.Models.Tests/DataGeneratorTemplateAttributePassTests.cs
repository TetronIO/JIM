using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Staging;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace JIM.Models.Tests
{
    public class DataGeneratorTemplateAttributePassTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestIsValidMVAttributePass()
        {
            var subject = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject.IsValid());
        }

        [Test]
        public void TestIsValidCSAttributePass()
        {
            var subject = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject.IsValid());
        }

        [Test]
        public void TestIsValidPopulatedValuesPercentagePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 1,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 50,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject3.IsValid());
        }

        [Test]
        public void TestIsValidNumberTypeSequentialPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MinNumber = 1
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MaxNumber = 50
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MaxNumber = 100
            };
            Assert.IsTrue(subject3.IsValid());

            var subject4 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true
            };
            Assert.IsTrue(subject4.IsValid());

            var subject5 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.IsTrue(subject5.IsValid());

            var subject6 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.IsTrue(subject6.IsValid());
        }

        [Test]
        public void TestIsValidNumberTypeRandomPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 1
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MaxNumber = 50
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.IsTrue(subject3.IsValid());
        }

        [Test]
        public void TestIsValidNumberTypeOnStringPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 1
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MaxNumber = 50
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.IsTrue(subject3.IsValid());

            var subject4 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.IsTrue(subject4.IsValid());

            var subject5 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MaxNumber = 50
            };
            Assert.IsTrue(subject5.IsValid());

            var subject6 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.IsTrue(subject6.IsValid());
        }

        [Test]
        public void TestIsValidBoolPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = true
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = false
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 1
            };
            Assert.IsTrue(subject3.IsValid());

            var subject4 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 50
            };
            Assert.IsTrue(subject4.IsValid());

            var subject5 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 100
            };
            Assert.IsTrue(subject5.IsValid());

            var subject6 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 100,
                BoolShouldBeRandom = true
            };
            Assert.IsTrue(subject6.IsValid());
        }

        [Test]
        public void TestIsValidDateTimePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MaxDate = DateTime.Now
            };
            Assert.IsTrue(subject3.IsValid());

            var subject4 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now,
                MaxDate = DateTime.Now.AddDays(10)
            };
            Assert.IsTrue(subject4.IsValid());
        }

        [Test]
        public void TestIsValidStringPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                ExampleDataSets = { new ExampleDataSet() }
            };
            Assert.IsTrue(subject2.IsValid());
        }

        [Test]
        public void TestExampleDataSetUsagePass()
        {
            // you can assign one or more ExampleDataSets with no pattern
            // you can assign one or more ExampleDAtaSets with a pattern

            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                ExampleDataSets = { new ExampleDataSet() }
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                ExampleDataSets = { new ExampleDataSet(), new ExampleDataSet() }
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                ExampleDataSets = { new ExampleDataSet(), new ExampleDataSet() },
                Pattern = "{0} {1}"
            };
            Assert.IsTrue(subject3.IsValid());
        }

        [Test]
        public void TestIsValidGuidPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Guid },
                PopulatedValuesPercentage = 100
            };
            Assert.IsTrue(subject1.IsValid());
        }

        [Test]
        public void TestIsValidReferencePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.StaticMembers },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100
            };
            Assert.IsTrue(subject1.IsValid());
        }

        [Test]
        public void TestIsValidMvaReferencePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMinAssignments = 10
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMaxAssignments = 10
            };
            Assert.IsTrue(subject2.IsValid());

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMinAssignments = 10,
                MvaRefMaxAssignments = 100
            };
            Assert.IsTrue(subject3.IsValid());
        }

        [Test]
        public void TestIsValidManagerPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
                ManagerDepthPercentage = 50
            };
            Assert.IsTrue(subject1.IsValid());

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
                ManagerDepthPercentage = 95
            };
            Assert.IsTrue(subject2.IsValid());
        }
    }
}