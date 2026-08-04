// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Tests the Activities list initiator-type and date-range filters: auditors need to isolate, for example,
/// user-made changes within a window, so the repository must filter on InitiatedByType and Created.
/// </summary>
[TestFixture]
public class ActivityListFilterTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _repository?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task GetActivitiesAsync_InitiatorTypeFilter_ReturnsOnlyMatchingInitiatorsAsync()
    {
        var userActivity = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        var apiKeyActivity = NewActivity(ActivityInitiatorType.ApiKey, daysOld: 1);
        var systemActivity = NewActivity(ActivityInitiatorType.System, daysOld: 1);
        _dbContext.Activities.AddRange(userActivity, apiKeyActivity, systemActivity);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100,
            initiatorTypeFilter: [ActivityInitiatorType.User, ActivityInitiatorType.ApiKey]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { userActivity.Id, apiKeyActivity.Id }),
            "only user- and API-key-initiated activities were requested");
    }

    [Test]
    public async Task GetActivitiesAsync_DateRange_ReturnsOnlyActivitiesWithinRangeAsync()
    {
        var tooOld = NewActivity(ActivityInitiatorType.User, daysOld: 30);
        var inRange = NewActivity(ActivityInitiatorType.User, daysOld: 5);
        var tooNew = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        _dbContext.Activities.AddRange(tooOld, inRange, tooNew);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100,
            createdFrom: DateTime.UtcNow.AddDays(-7),
            createdTo: DateTime.UtcNow.AddDays(-2));

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { inRange.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_OpenEndedDateRange_AppliesOnlyTheSuppliedBoundAsync()
    {
        var older = NewActivity(ActivityInitiatorType.User, daysOld: 30);
        var newer = NewActivity(ActivityInitiatorType.User, daysOld: 1);
        _dbContext.Activities.AddRange(older, newer);
        await _dbContext.SaveChangesAsync();

        var fromOnly = await _repository.Activity.GetActivitiesAsync(page: 1, pageSize: 100, createdFrom: DateTime.UtcNow.AddDays(-7));
        var toOnly = await _repository.Activity.GetActivitiesAsync(page: 1, pageSize: 100, createdTo: DateTime.UtcNow.AddDays(-7));

        Assert.That(fromOnly.Results.Select(a => a.Id), Is.EquivalentTo(new[] { newer.Id }));
        Assert.That(toOnly.Results.Select(a => a.Id), Is.EquivalentTo(new[] { older.Id }));
    }

    [Test]
    public async Task GetActivitiesAsync_ConnectedSystemFilter_ReturnsOnlyThoseSystemsActivitiesAsync()
    {
        var contoso = NewActivity(ActivityInitiatorType.User, daysOld: 1, targetContext: "Contoso AD");
        var fabrikam = NewActivity(ActivityInitiatorType.User, daysOld: 1, targetContext: "Fabrikam HR");
        var northwind = NewActivity(ActivityInitiatorType.User, daysOld: 1, targetContext: "Northwind SQL");
        _dbContext.Activities.AddRange(contoso, fabrikam, northwind);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100, connectedSystemFilter: ["Contoso AD", "Fabrikam HR"]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { contoso.Id, fabrikam.Id }),
            "the Connected System filter matches the Activity's Target Context, and is additive within itself");
    }

    [Test]
    public async Task GetActivitiesAsync_RunProfileFilter_ReturnsOnlyThoseRunProfilesActivitiesAsync()
    {
        var fullImport = NewActivity(ActivityInitiatorType.System, daysOld: 1, targetName: "Full Import");
        var deltaImport = NewActivity(ActivityInitiatorType.System, daysOld: 1, targetName: "Delta Import");
        _dbContext.Activities.AddRange(fullImport, deltaImport);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100, runProfileFilter: ["Full Import"]);

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { fullImport.Id }),
            "the Run Profile filter matches the Activity's Target Name");
    }

    [Test]
    public async Task GetActivitiesAsync_InitiatedByFilter_MatchesPartOfTheNameCaseInsensitivelyAsync()
    {
        var alice = NewActivity(ActivityInitiatorType.User, daysOld: 1, initiatedByName: "Alice Adams");
        var bob = NewActivity(ActivityInitiatorType.User, daysOld: 1, initiatedByName: "Bob Brown");
        _dbContext.Activities.AddRange(alice, bob);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100, initiatedByFilter: "ALICE");

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { alice.Id }),
            "the initiator filter is a case-insensitive partial match on the initiator's name, "
            + "distinct from initiatedById which matches an exact principal");
    }

    [Test]
    public async Task GetActivitiesAsync_InitiatedByFilterAndInitiatedById_CoexistAndNarrowEachOtherAsync()
    {
        var principalId = Guid.NewGuid();
        var alicesOwn = NewActivity(ActivityInitiatorType.User, daysOld: 1, initiatedByName: "Alice Adams");
        alicesOwn.InitiatedById = principalId;
        var alicesNamesake = NewActivity(ActivityInitiatorType.User, daysOld: 1, initiatedByName: "Alice Anderson");
        _dbContext.Activities.AddRange(alicesOwn, alicesNamesake);
        await _dbContext.SaveChangesAsync();

        var result = await _repository.Activity.GetActivitiesAsync(
            page: 1, pageSize: 100, initiatedById: principalId, initiatedByFilter: "alice");

        Assert.That(result.Results.Select(a => a.Id), Is.EquivalentTo(new[] { alicesOwn.Id }),
            "both initiator filters are supported and combine with AND");
    }

    private static Activity NewActivity(
        ActivityInitiatorType initiatorType,
        int daysOld,
        string? targetContext = null,
        string? targetName = null,
        string? initiatedByName = null) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        TargetContext = targetContext,
        TargetName = targetName,
        InitiatedByType = initiatorType,
        InitiatedByName = initiatedByName ?? initiatorType.ToString(),
        Created = DateTime.UtcNow.AddDays(-daysOld)
    };
}
