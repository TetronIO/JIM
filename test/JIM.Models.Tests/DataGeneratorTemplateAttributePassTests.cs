using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Staging;
using NUnit.Framework;
using System;

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
        public void TestIsValidNumberTypePass()
        {
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
                MinNumber = 1,
                MaxNumber = 1000
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
                ExampleDataSetId = 2,
                ExampleDataValueId = 5
            };
            Assert.IsTrue(subject2.IsValid());
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
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Reference },
                PopulatedValuesPercentage = 100
            };
            Assert.IsTrue(subject1.IsValid());
        }
    }
}