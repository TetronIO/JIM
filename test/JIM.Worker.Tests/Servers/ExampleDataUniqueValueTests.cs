// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.UniqueValues;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using JIM.Models.Utility;
using Moq;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Example data generates unique values through the Unique Value Generation service (#1789): <c>[UniqueInt]</c>
/// patterns, Example Data Set patterns and sequential numbers are unique against the Metaverse Objects that already
/// exist, not only against the values one template execution generated, while keeping the shapes example data has
/// always produced. Every object a run persists is written into the in-memory synchronisation repository, so a second
/// run sees the first run's values exactly as it would in the database.
/// </summary>
[TestFixture]
public class ExampleDataUniqueValueTests
{
    private const int TemplateId = 1;
    private const int DataSetId = 50;
    private const int FirstNameId = 200;
    private const int LastNameId = 201;
    private const int EmailId = 202;
    private const int LevelId = 203;
    private const int CompanyId = 204;

    private readonly MetaverseObjectType _userType = new() { Id = 1, Name = "User" };
    private readonly MetaverseAttribute _firstName = new() { Id = FirstNameId, Name = "First Name", Type = AttributeDataType.Text };
    private readonly MetaverseAttribute _lastName = new() { Id = LastNameId, Name = "Last Name", Type = AttributeDataType.Text };
    private readonly MetaverseAttribute _email = new() { Id = EmailId, Name = "Email", Type = AttributeDataType.Text };
    private readonly MetaverseAttribute _level = new() { Id = LevelId, Name = "Level", Type = AttributeDataType.Number };
    private readonly MetaverseAttribute _company = new() { Id = CompanyId, Name = "Company", Type = AttributeDataType.Text };

    private Mock<IExampleDataRepository> _mockExampleDataRepository = null!;
    private InMemorySyncRepository _syncRepository = null!;
    private JimApplication _application = null!;
    private List<MetaverseObject> _persistedObjects = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _persistedObjects = [];
        _syncRepository = new InMemorySyncRepository();

        _mockExampleDataRepository = new Mock<IExampleDataRepository>();
        _mockExampleDataRepository
            .Setup(r => r.CreateMetaverseObjectsAsync(It.IsAny<List<MetaverseObject>>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<Func<PersistenceProgress, Task>?>()))
            .Callback<List<MetaverseObject>, int, CancellationToken, Func<PersistenceProgress, Task>?>((objects, _, _, _) =>
            {
                foreach (var mvo in objects)
                {
                    if (mvo.Id == Guid.Empty)
                        mvo.Id = TimeOrderedIds.New();
                    _syncRepository.SeedMetaverseObject(mvo);
                }
                _persistedObjects.AddRange(objects);
            })
            .ReturnsAsync((List<MetaverseObject> objects, int _, CancellationToken _, Func<PersistenceProgress, Task>? _) => objects.Count);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepository.Object);
        repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);
        _application = new JimApplication(repository.Object, _syncRepository);
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    private ExampleDataObjectType UsersNamed(string firstName, string lastName, int count)
    {
        var objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = _userType, ObjectsToCreate = count };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 1, MetaverseAttribute = _firstName, PopulatedValuesPercentage = 100,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = firstName, Weight = 1 }]
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 2, MetaverseAttribute = _lastName, PopulatedValuesPercentage = 100,
            WeightedStringValues = [new ExampleDataTemplateAttributeWeightedValue { Value = lastName, Weight = 1 }]
        });
        return objectType;
    }

    private void UseTemplate(ExampleDataObjectType objectType)
    {
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Unique values" };
        template.ObjectTypes.Add(objectType);
        _mockExampleDataRepository.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
    }

    private void SeedExisting(int attributeId, string? text = null, int? number = null) =>
        _syncRepository.SeedMetaverseObject(new MetaverseObject
        {
            Id = TimeOrderedIds.New(),
            Type = _userType,
            AttributeValues = [new MetaverseObjectAttributeValue { AttributeId = attributeId, StringValue = text, IntValue = number }]
        });

    private Task<int> RunAsync() => _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None);

    private List<string> TextValuesOf(int attributeId, IEnumerable<MetaverseObject> objects) => objects
        .SelectMany(mvo => mvo.AttributeValues.Where(av => av.AttributeId == attributeId || av.Attribute?.Id == attributeId))
        .Select(av => av.StringValue!)
        .ToList();

    private List<int> NumbersOf(int attributeId, IEnumerable<MetaverseObject> objects) => objects
        .SelectMany(mvo => mvo.AttributeValues.Where(av => av.AttributeId == attributeId || av.Attribute?.Id == attributeId))
        .Select(av => av.IntValue!.Value)
        .OrderBy(n => n)
        .ToList();

    // ---- [UniqueInt] ----

    [Test]
    public async Task ExecuteTemplateAsync_UniqueIntValueHeldByAnExistingObject_SkipsItAsync()
    {
        SeedExisting(EmailId, text: "Ada.Lovelace@panoply.local");
        var users = UsersNamed("Ada", "Lovelace", 3);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        UseTemplate(users);

        await RunAsync();

        Assert.That(TextValuesOf(EmailId, _persistedObjects), Is.EquivalentTo(new[]
        {
            "Ada.Lovelace2@panoply.local", "Ada.Lovelace3@panoply.local", "Ada.Lovelace4@panoply.local"
        }), "an existing holder of the bare value, in any case, takes it out of use");
    }

    [Test]
    public async Task ExecuteTemplateAsync_UniqueIntWithinOneRun_FirstValueIsBareAndTheRestNumberedFromTwoAsync()
    {
        var users = UsersNamed("Ada", "Lovelace", 60);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        UseTemplate(users);

        await RunAsync();

        var expected = new[] { "Ada.Lovelace@panoply.local" }
            .Concat(Enumerable.Range(2, 59).Select(n => $"Ada.Lovelace{n}@panoply.local"));
        Assert.That(TextValuesOf(EmailId, _persistedObjects), Is.EquivalentTo(expected));
    }

    [Test]
    public async Task ExecuteTemplateAsync_UniqueIntNotBeforeTheAtSign_KeepsItsPositionInThePatternAsync()
    {
        var users = UsersNamed("Ada", "Lovelace", 2);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}[UniqueInt]-ext"
        });
        UseTemplate(users);

        await RunAsync();

        Assert.That(TextValuesOf(EmailId, _persistedObjects), Is.EquivalentTo(new[] { "Ada-ext", "Ada2-ext" }));
    }

    [Test]
    public async Task ExecuteTemplateAsync_RunTwice_SecondRunNeverRepeatsTheFirstRunsValuesAsync()
    {
        var users = UsersNamed("Ada", "Lovelace", 3);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        UseTemplate(users);

        await RunAsync();
        await RunAsync();

        var values = TextValuesOf(EmailId, _persistedObjects);
        Assert.That(values, Is.Unique.And.Count.EqualTo(6));
    }

    [Test]
    public async Task ExecuteTemplateAsync_UniqueValueReferencedByALaterAttribute_LaterAttributeSeesTheFinalValueAsync()
    {
        SeedExisting(EmailId, text: "Ada.Lovelace@panoply.local");
        var users = UsersNamed("Ada", "Lovelace", 1);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 4, MetaverseAttribute = _company, PopulatedValuesPercentage = 100, Pattern = "mailbox {Email}"
        });
        UseTemplate(users);

        await RunAsync();

        Assert.That(TextValuesOf(CompanyId, _persistedObjects), Is.EqualTo(new[] { "mailbox Ada.Lovelace2@panoply.local" }));
    }

    // ---- Example Data Set patterns ----

    private ExampleDataTemplateAttribute CompanyFromDataSet(params string[] companies)
    {
        var set = new ExampleDataSet { Id = DataSetId, Name = "Companies" };
        foreach (var company in companies)
            set.Values.Add(new ExampleDataSetValue { StringValue = company });
        _mockExampleDataRepository.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync(set);

        var attribute = new ExampleDataTemplateAttribute { Id = 5, MetaverseAttribute = _company, PopulatedValuesPercentage = 100, Pattern = "{0}" };
        attribute.ExampleDataSetInstances.Add(new ExampleDataSetInstance { Id = 1, ExampleDataTemplateAttribute = attribute, ExampleDataSet = set, Order = 0 });
        return attribute;
    }

    [Test]
    public async Task ExecuteTemplateAsync_DataSetValueHeldByAnExistingObject_PicksAnotherValueRatherThanAddingANumberAsync()
    {
        SeedExisting(CompanyId, text: "acme");
        var users = UsersNamed("Ada", "Lovelace", 1);
        users.TemplateAttributes.Add(CompanyFromDataSet("Acme", "Globex"));
        UseTemplate(users);

        await RunAsync();

        Assert.That(TextValuesOf(CompanyId, _persistedObjects), Is.EqualTo(new[] { "Globex" }));
    }

    [Test]
    public void ExecuteTemplateAsync_DataSetPatternRunsOutOfUnusedValues_FailsNamingTheAttributeAsync()
    {
        var users = UsersNamed("Ada", "Lovelace", 3);
        users.TemplateAttributes.Add(CompanyFromDataSet("Acme", "Globex"));
        UseTemplate(users);

        var ex = Assert.ThrowsAsync<InvalidDataException>(RunAsync);

        Assert.That(ex!.Message, Does.Contain("Company"));
    }

    // ---- Sequential numbers ----

    private ExampleDataObjectType UsersWithSequentialLevel(int count, int minNumber)
    {
        var users = UsersNamed("Ada", "Lovelace", count);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 6, MetaverseAttribute = _level, PopulatedValuesPercentage = 100, MinNumber = minNumber, SequentialNumbers = true
        });
        return users;
    }

    [Test]
    public async Task ExecuteTemplateAsync_SequentialNumbers_StartAtTheMinimumAndCountUpAsync()
    {
        UseTemplate(UsersWithSequentialLevel(4, 100));

        await RunAsync();

        Assert.That(NumbersOf(LevelId, _persistedObjects), Is.EqualTo(new[] { 100, 101, 102, 103 }));
    }

    [Test]
    public async Task ExecuteTemplateAsync_SequentialNumbersAlreadyHeld_ContinueAfterTheHighestAsync()
    {
        SeedExisting(LevelId, number: 1);
        SeedExisting(LevelId, number: 2);
        SeedExisting(LevelId, number: 3);
        UseTemplate(UsersWithSequentialLevel(4, 1));

        await RunAsync();

        Assert.That(NumbersOf(LevelId, _persistedObjects), Is.EqualTo(new[] { 4, 5, 6, 7 }));
    }

    [Test]
    public async Task ExecuteTemplateAsync_SequentialNumbersRunTwice_SecondRunContinuesTheSequenceAsync()
    {
        UseTemplate(UsersWithSequentialLevel(3, 1));

        await RunAsync();
        await RunAsync();

        Assert.That(NumbersOf(LevelId, _persistedObjects), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
    }

    // ---- Sharing the Worker's database context ----

    [Test]
    public async Task ExecuteTemplateAsync_ProgressWrittenWhileUniqueValuesAreChecked_NeverOverlapsTheChecksAsync()
    {
        // The progress reporter writes the Activity on the same database context the uniqueness checks query; on the
        // Worker the two overlapping throws "A second operation was started on this context instance". The reporter
        // here holds each write open for a while and reports as often as it can, so any overlap is all but certain.
        var writeInFlight = 0;
        var (repository, overlaps) = OverlapDetectingSyncRepositoryProxy.Create(_syncRepository, () => Volatile.Read(ref writeInFlight) == 1);
        var dataRepository = new Mock<IRepository>();
        dataRepository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepository.Object);
        dataRepository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);
        using var application = new JimApplication(dataRepository.Object, repository);

        var users = UsersNamed("Ada", "Lovelace", 3000);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        UseTemplate(users);

        await application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None,
            progressCallback: async (_, _, message) =>
            {
                // Only generation runs alongside the checks; holding the later phases' writes open would only slow the
                // test, since they report once an object.
                if (message?.StartsWith("Generating", StringComparison.Ordinal) != true)
                    return;

                Volatile.Write(ref writeInFlight, 1);
                await Task.Delay(20);
                Volatile.Write(ref writeInFlight, 0);
            },
            progressUpdateInterval: TimeSpan.FromMilliseconds(1));

        Assert.That(overlaps[0], Is.Zero, "a uniqueness check ran while a progress write held the shared database context");
    }

    // ---- The shared reservation set ----

    [Test]
    public async Task ExecuteTemplateAsync_ValueClaimedByAConcurrentRun_IsNotIssuedAsync()
    {
        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, EmailId, "ada.lovelace@panoply.local");
        var users = UsersNamed("Ada", "Lovelace", 1);
        users.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            Id = 3, MetaverseAttribute = _email, PopulatedValuesPercentage = 100, Pattern = "{First Name}.{Last Name}[UniqueInt]@panoply.local"
        });
        UseTemplate(users);

        await _application.ExampleData.ExecuteTemplateAsync(TemplateId, CancellationToken.None, uniqueValueReservations: reservations);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TextValuesOf(EmailId, _persistedObjects), Is.EqualTo(new[] { "Ada.Lovelace2@panoply.local" }));
            Assert.That(reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, EmailId, "ada.lovelace2@panoply.local"), Is.False,
                "the run releases its claims once its objects are persisted");
        }
    }
}
