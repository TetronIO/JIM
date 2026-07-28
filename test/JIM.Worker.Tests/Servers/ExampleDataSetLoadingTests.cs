// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Reproduces issue #1112: the template retrieval does not load Example Data Set values, and under a
/// NoTracking context (JIM.Web) EF identity-map fix-up never fills them in, so template execution must
/// substitute the separately-loaded, fully-populated sets before generation. Without that, pattern-based
/// attributes index into empty value collections and crash with ArgumentOutOfRangeException.
/// </summary>
[TestFixture]
public class ExampleDataSetLoadingTests
{
    private const int TemplateId = 1;
    private const int DataSetId = 9;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IExampleDataRepository> _mockExampleDataRepository = null!;
    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;
    private ExampleDataSet _fullyLoadedSet = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _persistedObjects = [];

        _fullyLoadedSet = new ExampleDataSet
        {
            Id = DataSetId,
            Name = "Colours",
            BuiltIn = true
        };
        foreach (var colour in new[] { "Red", "Blue", "Green", "Amber", "Violet", "Teal", "Coral", "Ochre", "Slate", "Jade" })
            _fullyLoadedSet.Values.Add(new ExampleDataSetValue { StringValue = colour });

        _mockExampleDataRepository = new Mock<IExampleDataRepository>();
        _mockExampleDataRepository
            .Setup(r => r.GetExampleDataSetAsync(DataSetId))
            .ReturnsAsync(_fullyLoadedSet);
        _mockExampleDataRepository
            .Setup(r => r.CreateMetaverseObjectsAsync(
                It.IsAny<List<MetaverseObject>>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>()))
            .Callback<List<MetaverseObject>, int, CancellationToken, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>?>(
                (objects, _, _, _) => _persistedObjects.AddRange(objects))
            .ReturnsAsync((List<MetaverseObject> objects, int _, CancellationToken _, Func<JIM.Models.ExampleData.DTOs.PersistenceProgress, Task>? _) => objects.Count);

        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    /// <summary>
    /// Builds a template whose data set instances carry an unloaded (empty-values) copy of the set,
    /// mirroring what <c>GetTemplateAsync</c> returns under a NoTracking context.
    /// </summary>
    private static ExampleDataTemplate NewTemplate(string? pattern)
    {
        var groupType = new MetaverseObjectType { Id = 2, Name = "Group" };
        var displayName = new MetaverseAttribute
        {
            Id = 100,
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text
        };

        var unloadedSet = new ExampleDataSet
        {
            Id = DataSetId,
            Name = "Colours",
            BuiltIn = true
            // Values deliberately empty: the template load does not include them.
        };

        var templateAttribute = new ExampleDataTemplateAttribute
        {
            Id = 1,
            MetaverseAttribute = displayName,
            Pattern = pattern,
            PopulatedValuesPercentage = 100
        };
        templateAttribute.ExampleDataSetInstances.Add(new ExampleDataSetInstance
        {
            Id = 1,
            ExampleDataTemplateAttribute = templateAttribute,
            ExampleDataSet = unloadedSet,
            Order = 0
        });

        var objectType = new ExampleDataObjectType
        {
            Id = 1,
            MetaverseObjectType = groupType,
            ObjectsToCreate = 3
        };
        objectType.TemplateAttributes.Add(templateAttribute);

        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Test Template" };
        template.ObjectTypes.Add(objectType);
        return template;
    }

    private void SetUpTemplate(ExampleDataTemplate template)
    {
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    [Test]
    public async Task ExecuteTemplateAsync_PatternAttributeWithUnloadedSetValues_GeneratesFromTheFullyLoadedSetAsync()
    {
        // The crashing case from #1112: Group names are pattern-generated ("{0} Team" style).
        SetUpTemplate(NewTemplate(pattern: "{0} Team"));

        var created = await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(created, Is.EqualTo(3));
        Assert.That(_persistedObjects, Has.Count.EqualTo(3));
        foreach (var value in _persistedObjects.Select(mvo => mvo.AttributeValues.Single(av => av.Attribute?.Id == 100).StringValue))
        {
            Assert.That(value, Does.EndWith(" Team"));
            var colour = value!.Replace(" Team", string.Empty);
            Assert.That(_fullyLoadedSet.Values.Select(v => v.StringValue), Does.Contain(colour));
        }
    }

    [Test]
    public async Task ExecuteTemplateAsync_SingleSetAttributeWithUnloadedSetValues_GeneratesFromTheFullyLoadedSetAsync()
    {
        // The non-pattern branch previously survived via a per-branch workaround; this guards the
        // behaviour now the substitution happens once, up front.
        SetUpTemplate(NewTemplate(pattern: null));

        var created = await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

        Assert.That(created, Is.EqualTo(3));
        foreach (var value in _persistedObjects.Select(mvo => mvo.AttributeValues.Single(av => av.Attribute?.Id == 100).StringValue))
        {
            Assert.That(_fullyLoadedSet.Values.Select(v => v.StringValue), Does.Contain(value));
        }
    }

    [Test]
    public void ExecuteTemplateAsync_ReferencedSetHasNoValuesAnywhere_ThrowsAClearErrorAsync()
    {
        // A genuinely empty set must fail fast with an actionable message naming the set, not an
        // ArgumentOutOfRangeException from deep inside a generation thread.
        _fullyLoadedSet.Values.Clear();
        SetUpTemplate(NewTemplate(pattern: "{0} Team"));

        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("Colours"));
    }
}
