// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Boolean template attribute's BoolTrueDistribution is the exact share of the objects given a value that are given
/// true (#877). Before, it was stored and ignored, and every Boolean was a coin toss.
/// </summary>
[TestFixture]
public class ExampleDataBooleanDistributionTests
{
    private const int TemplateId = 1;
    private const int FlagAttributeId = 200;
    private const int CategoryAttributeId = 201;
    private const int DependentAttributeId = 202;

    private Mock<IExampleDataRepository> _mockExampleDataRepository = null!;
    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;
    private MetaverseObjectType _userType = null!;
    private MetaverseAttribute _flag = null!;
    private MetaverseAttribute _category = null!;
    private MetaverseAttribute _dependent = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _persistedObjects = [];

        _mockExampleDataRepository = new Mock<IExampleDataRepository>();
        _mockExampleDataRepository
            .Setup(r => r.CreateMetaverseObjectsAsync(
                It.IsAny<List<MetaverseObject>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>()))
            .Callback<List<MetaverseObject>, int, CancellationToken, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>(
                (objects, _, _, _) => _persistedObjects.AddRange(objects))
            .ReturnsAsync((List<MetaverseObject> objects, int _, CancellationToken _, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>? _) => objects.Count);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);
        _application = new JimApplication(repository.Object);

        _userType = new MetaverseObjectType { Id = 1, Name = "User" };
        _flag = new MetaverseAttribute { Id = FlagAttributeId, Name = "Account Disabled", Type = AttributeDataType.Boolean };
        _category = new MetaverseAttribute { Id = CategoryAttributeId, Name = "Category", Type = AttributeDataType.Text };
        _dependent = new MetaverseAttribute { Id = DependentAttributeId, Name = "Disabled Reason", Type = AttributeDataType.Text };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    private ExampleDataObjectType SetUpTemplate(int objectsToCreate, params ExampleDataTemplateAttribute[] attributes)
    {
        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = _userType, ObjectsToCreate = objectsToCreate };
        objectType.TemplateAttributes.AddRange(attributes);

        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Booleans" };
        template.ObjectTypes.Add(objectType);
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
        return objectType;
    }

    private ExampleDataTemplateAttribute FlagAttribute(int? distribution, int? populated = 100) => new()
    {
        Id = 1, MetaverseAttribute = _flag, PopulatedValuesPercentage = populated, BoolTrueDistribution = distribution
    };

    private List<bool> FlagValues() => _persistedObjects
        .SelectMany(mvo => mvo.AttributeValues.Where(av => av.Attribute.Id == FlagAttributeId))
        .Select(av => av.BoolValue!.Value)
        .ToList();

    [TestCase(200, 20, 40)]
    [TestCase(1000, 5, 50)]
    [TestCase(200, 0, 0)]
    [TestCase(200, 100, 200)]
    [TestCase(7, 50, 4)]      // 3.5 rounds half up
    [TestCase(10, 15, 2)]     // 1.5 rounds half up
    [TestCase(3, 10, 0)]      // 0.3 rounds down
    [TestCase(1, 50, 1)]      // 0.5 rounds half up
    public async Task ExecuteTemplateAsync_BoolTrueDistribution_GivesExactlyTheQuotaTrueAsync(int objects, int distribution, int expectedTrue)
    {
        SetUpTemplate(objects, FlagAttribute(distribution));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var values = FlagValues();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(objects));
            Assert.That(values.Count(v => v), Is.EqualTo(expectedTrue));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_BoolTrueDistributionWithPartialPopulation_QuotaHoldsOverObjectsGivenAValueAsync()
    {
        // 200 objects, half given a value, a quarter of those true: exactly 100 values and 25 true.
        SetUpTemplate(200, FlagAttribute(distribution: 25, populated: 50));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var values = FlagValues();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(100));
            Assert.That(values.Count(v => v), Is.EqualTo(25));
            Assert.That(_persistedObjects, Has.Count.EqualTo(200));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_BoolTrueDistributionOnConditionalAttribute_QuotaHoldsOverObjectsWhoseConditionMetAsync()
    {
        // Roughly half the users are Staff; only Staff get the flag, and a fifth of those get true. Whatever the Staff
        // count turns out to be, the true count is the quota of it.
        var category = new ExampleDataTemplateAttribute
        {
            Id = 2, MetaverseAttribute = _category, PopulatedValuesPercentage = 100,
            WeightedStringValues =
            [
                new ExampleDataTemplateAttributeWeightedValue { Value = "Staff", Weight = 1 },
                new ExampleDataTemplateAttributeWeightedValue { Value = "Guest", Weight = 1 }
            ]
        };
        var flag = FlagAttribute(20);
        flag.AttributeDependency = new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = _category, ComparisonType = SearchComparisonType.Equals, StringValue = "Staff"
        };
        SetUpTemplate(500, flag, category);

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var staffCount = _persistedObjects.Count(mvo => mvo.AttributeValues.Any(av => av.Attribute.Id == CategoryAttributeId && av.StringValue == "Staff"));
        var values = FlagValues();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(staffCount));
            Assert.That(values.Count(v => v), Is.EqualTo(ExampleDataServer.CalculateBooleanTrueCount(staffCount, 20)));
        }
    }

    [TestCase(500, 50, 20)]
    [TestCase(1001, 30, 25)]
    public async Task ExecuteTemplateAsync_BoolTrueDistributionOnConditionalAttributeWithPartialPopulation_EmptyShareAndQuotaOverConditionHoldersAsync(
        int objects, int populated, int distribution)
    {
        // Only Staff qualify for the flag. Population % leaves exactly its share of Staff empty (#2448), and the quota
        // applies to the Staff that remain, so both counts are exact whatever the Staff count turns out to be.
        var category = new ExampleDataTemplateAttribute
        {
            Id = 2, MetaverseAttribute = _category, PopulatedValuesPercentage = 100,
            WeightedStringValues =
            [
                new ExampleDataTemplateAttributeWeightedValue { Value = "Staff", Weight = 1 },
                new ExampleDataTemplateAttributeWeightedValue { Value = "Guest", Weight = 1 }
            ]
        };
        var flag = FlagAttribute(distribution, populated);
        flag.AttributeDependency = new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = _category, ComparisonType = SearchComparisonType.Equals, StringValue = "Staff"
        };
        SetUpTemplate(objects, flag, category);

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var staffCount = _persistedObjects.Count(mvo => mvo.AttributeValues.Any(av => av.Attribute.Id == CategoryAttributeId && av.StringValue == "Staff"));
        var guestsWithFlag = _persistedObjects.Count(mvo =>
            mvo.AttributeValues.All(av => av.Attribute.Id != CategoryAttributeId || av.StringValue != "Staff") &&
            mvo.AttributeValues.Any(av => av.Attribute.Id == FlagAttributeId));
        var expectedWithValue = staffCount - staffCount * (100 - populated) / 100;
        var values = FlagValues();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(guestsWithFlag, Is.Zero);
            Assert.That(values, Has.Count.EqualTo(expectedWithValue));
            Assert.That(values.Count(v => v), Is.EqualTo(ExampleDataServer.CalculateBooleanTrueCount(expectedWithValue, distribution)));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_AttributeDependingOnDistributedBoolean_SeesTheFinalValueAsync()
    {
        // The reason is generated only for disabled accounts. Because the quota is settled before the dependent
        // attribute is generated, every disabled account, and only those, has a reason.
        var dependent = new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _dependent, PopulatedValuesPercentage = 100,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = "Left", Weight = 1 }],
            AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                MetaverseAttribute = _flag, ComparisonType = SearchComparisonType.Equals, StringValue = "true"
            }
        };
        SetUpTemplate(300, dependent, FlagAttribute(10));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var disabledWithoutReason = _persistedObjects.Count(mvo =>
            mvo.AttributeValues.Any(av => av.Attribute.Id == FlagAttributeId && av.BoolValue == true) &&
            mvo.AttributeValues.All(av => av.Attribute.Id != DependentAttributeId));
        var enabledWithReason = _persistedObjects.Count(mvo =>
            mvo.AttributeValues.Any(av => av.Attribute.Id == FlagAttributeId && av.BoolValue == false) &&
            mvo.AttributeValues.Any(av => av.Attribute.Id == DependentAttributeId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FlagValues().Count(v => v), Is.EqualTo(30));
            Assert.That(disabledWithoutReason, Is.Zero);
            Assert.That(enabledWithReason, Is.Zero);
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_NoBoolTrueDistribution_GivesBothValuesAtRandomAsync()
    {
        SetUpTemplate(200, FlagAttribute(null));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var values = FlagValues();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(200));
            Assert.That(values, Does.Contain(true).And.Contain(false));
        }
    }

    [TestCase(0, 50, 0)]
    [TestCase(200, 20, 40)]
    [TestCase(7, 50, 4)]
    [TestCase(3, 10, 0)]
    [TestCase(3, 17, 1)]
    [TestCase(99, 100, 99)]
    [TestCase(99, 0, 0)]
    public void CalculateBooleanTrueCount_RoundsToNearestWithHalvesUp(int valueCount, int percentage, int expected)
    {
        Assert.That(ExampleDataServer.CalculateBooleanTrueCount(valueCount, percentage), Is.EqualTo(expected));
    }
}
