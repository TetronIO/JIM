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
/// Example Data dependencies use the operator catalogue and the comparison scoping uses (#2053). Before, six of the
/// seven advertised operators threw <see cref="NotSupportedException"/> at generation time, and the one that worked
/// compared the dependent attribute's value as text whatever its type.
/// </summary>
[TestFixture]
public class ExampleDataDependencyTests
{
    private const int TemplateId = 1;
    private const int LevelAttributeId = 100;
    private const int TypeAttributeId = 101;
    private const int DependentAttributeId = 102;

    private Mock<IExampleDataRepository> _mockExampleDataRepository = null!;
    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;

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
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    /// <summary>
    /// Four users: Level is sequential from 1 (1, 2, 3, 4), Employee Type is always "Contractor", and Badge is generated
    /// only when the dependency holds.
    /// </summary>
    private void SetUpTemplate(int dependsOnAttributeId, SearchComparisonType op, string value)
    {
        var userType = new MetaverseObjectType { Id = 1, Name = "User" };
        var level = new MetaverseAttribute { Id = LevelAttributeId, Name = "Level", Type = AttributeDataType.Number };
        var employeeType = new MetaverseAttribute { Id = TypeAttributeId, Name = "Employee Type", Type = AttributeDataType.Text };
        var badge = new MetaverseAttribute { Id = DependentAttributeId, Name = "Badge", Type = AttributeDataType.Text };

        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = userType, ObjectsToCreate = 4 };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 1, MetaverseAttribute = level, PopulatedValuesPercentage = 100, MinNumber = 1, SequentialNumbers = true
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 2, MetaverseAttribute = employeeType, PopulatedValuesPercentage = 100,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = "Contractor", Weight = 1 }]
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = badge, PopulatedValuesPercentage = 100, Pattern = "B-[UniqueInt]",
            AttributeDependency = new ExampleDataTemplateAttributeDependency
            {
                MetaverseAttribute = dependsOnAttributeId == LevelAttributeId ? level : employeeType,
                ComparisonType = op,
                StringValue = value
            }
        });

        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Dependencies" };
        template.ObjectTypes.Add(objectType);
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    /// <summary>The Level of every generated user that received a Badge, sorted.</summary>
    private List<int> LevelsWithABadge() => _persistedObjects
        .Where(mvo => mvo.AttributeValues.Any(av => av.Attribute.Id == DependentAttributeId))
        .Select(mvo => mvo.AttributeValues.Single(av => av.Attribute.Id == LevelAttributeId).IntValue!.Value)
        .OrderBy(l => l)
        .ToList();

    [TestCase(SearchComparisonType.Equals, "3", new[] { 3 })]
    [TestCase(SearchComparisonType.NotEquals, "3", new[] { 1, 2, 4 })]
    [TestCase(SearchComparisonType.LessThan, "3", new[] { 1, 2 })]
    [TestCase(SearchComparisonType.LessThanOrEquals, "3", new[] { 1, 2, 3 })]
    [TestCase(SearchComparisonType.GreaterThan, "3", new[] { 4 })]
    [TestCase(SearchComparisonType.GreaterThanOrEquals, "3", new[] { 3, 4 })]
    [TestCase(SearchComparisonType.HasValue, "", new[] { 1, 2, 3, 4 })]
    [TestCase(SearchComparisonType.HasNoValue, "", new int[0])]
    public async Task ExecuteTemplateAsync_NumberDependency_ComparesAsANumberAsync(SearchComparisonType op, string value, int[] expected)
    {
        SetUpTemplate(LevelAttributeId, op, value);

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(LevelsWithABadge(), Is.EqualTo(expected));
    }

    [TestCase(SearchComparisonType.Equals, "Contractor", true)]
    [TestCase(SearchComparisonType.Equals, "contractor", false)]
    [TestCase(SearchComparisonType.StartsWith, "Contr", true)]
    [TestCase(SearchComparisonType.NotContains, "tract", false)]
    [TestCase(SearchComparisonType.EndsWith, "tor", true)]
    [TestCase(SearchComparisonType.MatchesPattern, "^C[a-z]+r$", true)]
    [TestCase(SearchComparisonType.NotMatchesPattern, "^Emp", true)]
    public async Task ExecuteTemplateAsync_TextDependency_ComparesCaseSensitivelyAsync(SearchComparisonType op, string value, bool generated)
    {
        SetUpTemplate(TypeAttributeId, op, value);

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(LevelsWithABadge(), Has.Count.EqualTo(generated ? 4 : 0));
    }

    [Test]
    public void ExecuteTemplateAsync_DependencyOperatorInvalidForTheAttributesType_FailsBeforeGeneratingAsync()
    {
        // Persisted before validation existed, or written outside it: fail with the attribute named, never guess.
        SetUpTemplate(LevelAttributeId, SearchComparisonType.StartsWith, "3");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("Badge").And.Contain("Level"));
    }
}
