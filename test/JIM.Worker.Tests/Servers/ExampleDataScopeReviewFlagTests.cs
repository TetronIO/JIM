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
/// Example data generation creates Metaverse Objects outside any synchronisation, so it must flag each one
/// <see cref="MetaverseObject.ScopeReviewPending"/>: otherwise an export rule that already exists never provisions
/// them until something unrelated touches them (#2615). The persistence layer writes the flag as given.
/// </summary>
[TestFixture]
public class ExampleDataScopeReviewFlagTests
{
    private const int TemplateId = 1;

    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _persistedObjects = [];

        var exampleDataRepository = new Mock<IExampleDataRepository>();
        exampleDataRepository
            .Setup(r => r.CreateMetaverseObjectsAsync(
                It.IsAny<List<MetaverseObject>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>()))
            .Callback<List<MetaverseObject>, int, CancellationToken, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>(
                (objects, _, _, _) => _persistedObjects.AddRange(objects))
            .ReturnsAsync((List<MetaverseObject> objects, int _, CancellationToken _, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>? _) => objects.Count);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ExampleData).Returns(exampleDataRepository.Object);
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
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Scope review" };
        template.ObjectTypes.Add(objectType);
        exampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    [Test]
    public async Task ExecuteTemplateAsync_GeneratedObjects_AreAllFlaggedForExportScopeReviewAsync()
    {
        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(_persistedObjects, Has.Count.EqualTo(3));
        Assert.That(_persistedObjects.Select(mvo => mvo.ScopeReviewPending), Is.All.True);
    }
}
