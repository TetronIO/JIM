// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A template attribute's Population % leaves exactly that share of the objects with a value (#2437). Before, the objects
/// to leave empty were drawn with replacement, so a repeat pick removed nothing and too many values survived (50% of 200
/// objects kept about 124 values rather than 100).
/// </summary>
[TestFixture]
public class ExampleDataPopulationPercentageTests
{
    private const int TemplateId = 1;
    private const int TextAttributeId = 300;
    private const int NumberAttributeId = 301;
    private const int ReferenceAttributeId = 302;
    private const int CategoryAttributeId = 303;
    private const int DepartmentNameAttributeId = 304;

    private Mock<IExampleDataRepository> _mockExampleDataRepository = null!;
    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;
    private MetaverseObjectType _userType = null!;
    private MetaverseObjectType _departmentType = null!;
    private MetaverseAttribute _text = null!;
    private MetaverseAttribute _number = null!;
    private MetaverseAttribute _departments = null!;
    private MetaverseAttribute _category = null!;
    private MetaverseAttribute _departmentName = null!;

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
        _departmentType = new MetaverseObjectType { Id = 2, Name = "Department" };
        _text = new MetaverseAttribute { Id = TextAttributeId, Name = "Job Title", Type = AttributeDataType.Text };
        _number = new MetaverseAttribute { Id = NumberAttributeId, Name = "Desk Number", Type = AttributeDataType.Number };
        _departments = new MetaverseAttribute
        {
            Id = ReferenceAttributeId, Name = "Departments", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
        };
        _category = new MetaverseAttribute { Id = CategoryAttributeId, Name = "Category", Type = AttributeDataType.Text };
        _departmentName = new MetaverseAttribute { Id = DepartmentNameAttributeId, Name = "Department Name", Type = AttributeDataType.Text };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    private void SetUpTemplate(params ExampleDataObjectType[] objectTypes)
    {
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Population" };
        template.ObjectTypes.AddRange(objectTypes);
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    private ExampleDataObjectType UserType(int objectsToCreate, params ExampleDataTemplateAttribute[] attributes)
    {
        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = _userType, ObjectsToCreate = objectsToCreate };
        objectType.TemplateAttributes.AddRange(attributes);
        return objectType;
    }

    private static List<ExampleDataTemplateAttributeWeightedValue> Values(params string[] values) =>
        values.Select(v => new ExampleDataTemplateAttributeWeightedValue { Value = v, Weight = 1 }).ToList();

    private ExampleDataTemplateAttribute TextAttribute(int populated) => new()
    {
        Id = 1, MetaverseAttribute = _text, PopulatedValuesPercentage = populated, WeightedStringValues = Values("Engineer", "Analyst")
    };

    private ExampleDataTemplateAttribute CategoryAttribute(params string[] values) => new()
    {
        Id = 2, MetaverseAttribute = _category, PopulatedValuesPercentage = 100, WeightedStringValues = Values(values)
    };

    private int UsersWithValue(int attributeId) => _persistedObjects
        .Count(mvo => mvo.Type == _userType && mvo.AttributeValues.Any(av => av.Attribute.Id == attributeId));

    private int StaffCount() => _persistedObjects
        .Count(mvo => mvo.AttributeValues.Any(av => av.Attribute.Id == CategoryAttributeId && av.StringValue == "Staff"));

    [TestCase(200, 50, 100)]
    [TestCase(200, 100, 200)]
    [TestCase(200, 1, 2)]
    [TestCase(200, 99, 198)]
    [TestCase(50, 1, 1)]      // 49.5 objects to leave empty rounds down, so the one remaining object keeps its value
    [TestCase(7, 50, 4)]      // 3.5 objects to leave empty rounds down
    public async Task ExecuteTemplateAsync_TextAttributeWithPopulation_LeavesExactlyThatShareWithAValueAsync(int objects, int populated, int expectedWithValue)
    {
        SetUpTemplate(UserType(objects, TextAttribute(populated)));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_persistedObjects, Has.Count.EqualTo(objects));
            Assert.That(UsersWithValue(TextAttributeId), Is.EqualTo(expectedWithValue));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_NumberAttributeWithPopulation_LeavesExactlyThatShareWithAValueAsync()
    {
        var number = new ExampleDataTemplateAttribute
        {
            Id = 1, MetaverseAttribute = _number, PopulatedValuesPercentage = 50, MinNumber = 1, MaxNumber = 1000, RandomNumbers = true
        };
        SetUpTemplate(UserType(200, number));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(UsersWithValue(NumberAttributeId), Is.EqualTo(100));
    }

    [Test]
    public async Task ExecuteTemplateAsync_MultiValuedReferenceWithPopulation_LeavesExactlyThatShareWithAllTheirValuesAsync()
    {
        // Each user is given two or three departments. Leaving a user empty removes all of them, so the users that keep
        // a value keep every value they were given.
        var departmentType = new ExampleDataObjectType { Id = 2, MetaverseObjectType = _departmentType, ObjectsToCreate = 20 };
        departmentType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 10, MetaverseAttribute = _departmentName, PopulatedValuesPercentage = 100, WeightedStringValues = Values("Sales", "Finance")
        });
        var departments = new ExampleDataTemplateAttribute
        {
            Id = 1, MetaverseAttribute = _departments, PopulatedValuesPercentage = 50,
            MvaRefMinAssignments = 2, MvaRefMaxAssignments = 3, ReferenceMetaverseObjectTypes = [_departmentType]
        };
        SetUpTemplate(departmentType, UserType(200, departments));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var valueCounts = _persistedObjects
            .Where(mvo => mvo.Type == _userType)
            .Select(mvo => mvo.AttributeValues.Count(av => av.Attribute.Id == ReferenceAttributeId))
            .Where(count => count > 0)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valueCounts, Has.Count.EqualTo(100));
            Assert.That(valueCounts, Has.All.InRange(2, 3));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_ConditionalAttributeWhoseConditionAlwaysHolds_LeavesExactlyThatShareWithAValueAsync()
    {
        var text = TextAttribute(50);
        text.AttributeDependency = new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = _category, ComparisonType = SearchComparisonType.Equals, StringValue = "Staff"
        };
        SetUpTemplate(UserType(200, text, CategoryAttribute("Staff")));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StaffCount(), Is.EqualTo(200));
            Assert.That(UsersWithValue(TextAttributeId), Is.EqualTo(100));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_ConditionalAttributeWithPopulation_LeavesThatShareOfConditionHoldersWithAValueAsync()
    {
        // Population % is applied over every object of the type, matching a distributed Boolean (#877), so the objects
        // whose condition holds keep a value in that share on average rather than exactly. With 4,000 objects, about
        // 2,000 Staff and 50% population, the standard deviation of the number kept is about 16; the band below is
        // about six of those wide on each side, while drawing with replacement kept about 61% of Staff (some 210 over).
        var text = TextAttribute(50);
        text.AttributeDependency = new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = _category, ComparisonType = SearchComparisonType.Equals, StringValue = "Staff"
        };
        SetUpTemplate(UserType(4000, text, CategoryAttribute("Staff", "Guest")));

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        var staffCount = StaffCount();
        var staffWithValue = _persistedObjects.Count(mvo =>
            mvo.AttributeValues.Any(av => av.Attribute.Id == CategoryAttributeId && av.StringValue == "Staff") &&
            mvo.AttributeValues.Any(av => av.Attribute.Id == TextAttributeId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(UsersWithValue(TextAttributeId), Is.EqualTo(staffWithValue), "Only objects whose condition holds may have a value.");
            Assert.That(staffWithValue, Is.InRange(staffCount / 2 - 100, staffCount / 2 + 100));
        }
    }
}
