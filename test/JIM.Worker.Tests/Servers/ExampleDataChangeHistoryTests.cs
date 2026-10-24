// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.ExampleData;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Created change records example data generation writes for the objects it makes (#2460).
/// </summary>
[TestFixture]
public class ExampleDataChangeHistoryTests
{
    private const int TemplateId = 1;

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

        // No settings rows exist, so every Service Setting read falls back to its default: Metaverse Object change
        // tracking on.
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);
        _application = new JimApplication(repository.Object);

        var jobTitle = new MetaverseAttribute { Id = 10, Name = "Job Title", Type = AttributeDataType.Text };
        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" }, ObjectsToCreate = 3 };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 1,
            MetaverseAttribute = jobTitle,
            PopulatedValuesPercentage = 100,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = "Engineer", Weight = 1 }]
        });
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Change history" };
        template.ObjectTypes.Add(objectType);
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    [Test]
    public async Task ExecuteTemplateAsync_WithActivity_CreatedChangeRecordsNameTheActivityAsync()
    {
        var activityId = Guid.NewGuid();

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None, activityId: activityId);

        var changes = _persistedObjects.SelectMany(mvo => mvo.Changes).ToList();
        Assert.That(changes, Has.Count.EqualTo(3));
        Assert.That(changes.Select(c => c.ActivityId), Is.All.EqualTo(activityId));
    }
}
