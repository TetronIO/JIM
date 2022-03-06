using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Staging;
using NUnit.Framework;
using System;

namespace JIM.Models.Tests
{
    public class DataGeneratorTemplateAttributeFailTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestIsValidAttributeFail()
        {
            var subject = new DataGeneratorTemplateAttribute
            {
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject.IsValid());
        }

        [Test]
        public void TestIsValidPopulatedValuesPercentageTooLowFail()
        {
            var subject = new DataGeneratorTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 0,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject.IsValid());
        }

        [Test]
        public void TestIsValidPopulatedValuesPercentageTooHighFail()
        {
            var subject = new DataGeneratorTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 101,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject.IsValid());
        }

        [Test]
        public void TestIsValidNumberTypeFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MinNumber = 100,
                MaxNumber = 50
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                RandomNumbers = true
            };
            Assert.IsFalse(subject2.IsValid());
        }

        [Test]
        public void TestIsValidDateTimeFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now.AddDays(1),
                MaxDate = DateTime.Now
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now,
                MaxDate = DateTime.Now.AddDays(-1)
            };
            Assert.IsFalse(subject2.IsValid());
        }

        [Test]
        public void TestIsValidStringFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value",
                ExampleDataSetId = 2,
                ExampleDataValueId = 5
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                ExampleDataSetId = 2
            };
            Assert.IsFalse(subject2.IsValid());
        }

        [Test]
        public void TestIsValidNumberMismatchFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now                
            };
            Assert.IsFalse(subject2.IsValid());

            var subject3 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                ExampleDataSetId = 1,
                ExampleDataValueId = 2
            };
            Assert.IsFalse(subject3.IsValid());

            var subject4 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = true
            };
            Assert.IsFalse(subject4.IsValid());
        }

        [Test]
        public void TestIsValidBoolMismatchFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now
            };
            Assert.IsFalse(subject2.IsValid());

            var subject3 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                ExampleDataSetId = 1,
                ExampleDataValueId = 2
            };
            Assert.IsFalse(subject3.IsValid());

            var subject4 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.Bool },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true
            };
            Assert.IsFalse(subject4.IsValid());
        }

        [Test]
        public void TestIsValidDateTimeMismatchFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = true
            };
            Assert.IsFalse(subject2.IsValid());

            var subject3 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                ExampleDataSetId = 1,
                ExampleDataValueId = 2
            };
            Assert.IsFalse(subject3.IsValid());

            var subject4 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true
            };
            Assert.IsFalse(subject4.IsValid());
        }

        [Test]
        public void TestIsValidStringMismatchFail()
        {
            var subject1 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = true
            };
            Assert.IsFalse(subject1.IsValid());

            var subject2 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true
            };
            Assert.IsFalse(subject2.IsValid());

            var subject3 = new DataGeneratorTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemAttribute { Type = AttributeDataType.String },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.Now
            };
            Assert.IsFalse(subject3.IsValid());
        }
    }
}