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
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };

            Assert.DoesNotThrow(subject.Validate);
        }

        [Test]
        public void TestIsValidCSAttributePass()
        {
            var subject = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.DoesNotThrow(subject.Validate);
        }

        [Test]
        public void TestIsValidPopulatedValuesPercentagePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 1,
                Pattern = "dummy-value"
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 50,
                Pattern = "dummy-value"
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.DoesNotThrow(subject3.Validate);
        }

        [Test]
        public void TestIsValidNumberTypeSequentialPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MaxNumber = 50
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                MaxNumber = 100
            };
            Assert.DoesNotThrow(subject3.Validate);

            var subject4 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true
            };
            Assert.DoesNotThrow(subject4.Validate);

            var subject5 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject5.Validate);

            var subject6 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject6.Validate);
        }

        [Test]
        public void TestIsValidNumberTypeRandomPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MaxNumber = 50
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Number },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.DoesNotThrow(subject3.Validate);
        }

        [Test]
        public void TestIsValidNumberTypeOnStringPass()
        {
            // numbers can be assigned to attributes of type number AND string
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MaxNumber = 50
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                RandomNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.DoesNotThrow(subject3.Validate);

            var subject4 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 1
            };
            Assert.DoesNotThrow(subject4.Validate);

            var subject5 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MaxNumber = 50
            };
            Assert.DoesNotThrow(subject5.Validate);

            var subject6 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                SequentialNumbers = true,
                MinNumber = 0,
                MaxNumber = 100
            };
            Assert.DoesNotThrow(subject6.Validate);
        }

        [Test]
        public void TestIsValidBoolPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = true
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolShouldBeRandom = false
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 1
            };
            Assert.DoesNotThrow(subject3.Validate);

            var subject4 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 50
            };
            Assert.DoesNotThrow(subject4.Validate);

            var subject5 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 100
            };
            Assert.DoesNotThrow(subject5.Validate);

            var subject6 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Boolean },
                PopulatedValuesPercentage = 100,
                BoolTrueDistribution = 100,
                BoolShouldBeRandom = true
            };
            Assert.DoesNotThrow(subject6.Validate);
        }

        [Test]
        public void TestIsValidDateTimePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.UtcNow
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MaxDate = DateTime.UtcNow
            };
            Assert.DoesNotThrow(subject3.Validate);

            var subject4 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.DateTime },
                PopulatedValuesPercentage = 100,
                MinDate = DateTime.UtcNow,
                MaxDate = DateTime.UtcNow.AddDays(10)
            };
            Assert.DoesNotThrow(subject4.Validate);
        }

        [Test]
        public void TestIsValidStringPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                Pattern = "dummy-value"
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance() }
            };
            Assert.DoesNotThrow(subject2.Validate);
        }

        [Test]
        public void TestIsValidWeightedStringValuesPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                MetaverseAttribute = new MetaverseAttribute { Type = AttributeDataType.Text },
                WeightedStringValues = new List<DataGenerationTemplateAttributeWeightedValue>
                {
                    new DataGenerationTemplateAttributeWeightedValue { Value = "Active", Weight = 0.85f },
                    new DataGenerationTemplateAttributeWeightedValue { Value = "Suspended", Weight = 0.1f },
                    new DataGenerationTemplateAttributeWeightedValue { Value = "Leaver", Weight = 0.05f }
                },
                PopulatedValuesPercentage = 100
            };

            Assert.DoesNotThrow(subject1.Validate);
        }

        [Test]
        public void TestExampleDataSetUsagePass()
        {
            // you can assign one or more ExampleDataSets with no pattern
            // you can assign one or more ExampleDAtaSets with a pattern

            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance() }
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance(), new ExampleDataSetInstance() }
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Text },
                PopulatedValuesPercentage = 100,
                ExampleDataSetInstances = new List<ExampleDataSetInstance> { new ExampleDataSetInstance(), new ExampleDataSetInstance() },
                Pattern = "{0} {1}"
            };
            Assert.DoesNotThrow(subject3.Validate);
        }

        [Test]
        public void TestIsValidGuidPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Guid },
                PopulatedValuesPercentage = 100
            };
            Assert.DoesNotThrow(subject1.Validate);
        }

        [Test]
        public void TestIsValidReferencePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.StaticMembers },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100
            };
            Assert.DoesNotThrow(subject1.Validate);
        }

        [Test]
        public void TestIsValidMvaReferencePass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMinAssignments = 10
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMaxAssignments = 10
            };
            Assert.DoesNotThrow(subject2.Validate);

            var subject3 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                ReferenceMetaverseObjectTypes = new List<MetaverseObjectType> { new MetaverseObjectType() },
                PopulatedValuesPercentage = 100,
                MvaRefMinAssignments = 10,
                MvaRefMaxAssignments = 100
            };
            Assert.DoesNotThrow(subject3.Validate);
        }

        [Test]
        public void TestIsValidManagerPass()
        {
            var subject1 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
                ManagerDepthPercentage = 50
            };
            Assert.DoesNotThrow(subject1.Validate);

            var subject2 = new DataGenerationTemplateAttribute
            {
                ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Type = AttributeDataType.Reference, Name = Constants.BuiltInAttributes.Manager },
                ManagerDepthPercentage = 95
            };
            Assert.DoesNotThrow(subject2.Validate);
        }
    }
}