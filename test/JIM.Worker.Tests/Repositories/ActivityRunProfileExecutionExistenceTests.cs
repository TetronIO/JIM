// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Covers <c>HasAnyRunProfileExecutionAsync</c>: the existence probe behind the home page's "Run your first
/// synchronisation" checklist step. The trap it has to avoid is that Run Profile *configuration* Activities
/// (create, update, delete) carry the same <see cref="ActivityTargetType.ConnectedSystemRunProfile"/> target type
/// as executions, so only <see cref="ActivityTargetOperationType.Execute"/> counts as having run one.
/// </summary>
[TestFixture]
public class ActivityRunProfileExecutionExistenceTests
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
    public async Task HasAnyRunProfileExecutionAsync_WhenNoActivitiesExist_ReturnsFalseAsync()
    {
        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.False);
    }

    [Test]
    public async Task HasAnyRunProfileExecutionAsync_WhenARunProfileHasBeenExecuted_ReturnsTrueAsync()
    {
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Execute,
            ActivityStatus.Complete);

        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.True);
    }

    [Test]
    public async Task HasAnyRunProfileExecutionAsync_WhenAnExecutionIsStillInProgress_ReturnsTrueAsync()
    {
        // A run that has started counts: the administrator has done the thing the checklist asked for, and the
        // step must not un-tick itself while the run is in flight.
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Execute,
            ActivityStatus.InProgress);

        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.True);
    }

    [Test]
    public async Task HasAnyRunProfileExecutionAsync_WhenAnExecutionFailed_ReturnsTrueAsync()
    {
        // A failed run was still run. The checklist step is "have you run one", not "did it succeed"; the run's
        // outcome is the Activity list's business.
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Execute,
            ActivityStatus.FailedWithError);

        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.True);
    }

    [Test]
    public async Task HasAnyRunProfileExecutionAsync_WhenOnlyRunProfileConfigurationChangesExist_ReturnsFalseAsync()
    {
        // Creating, editing and deleting Run Profiles all record ConnectedSystemRunProfile-typed Activities.
        // None of them is a synchronisation run.
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Create,
            ActivityStatus.Complete);
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Update,
            ActivityStatus.Complete);
        await SeedActivityAsync(
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetOperationType.Delete,
            ActivityStatus.Complete);

        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.False);
    }

    [Test]
    public async Task HasAnyRunProfileExecutionAsync_WhenOnlyOtherExecutionsExist_ReturnsFalseAsync()
    {
        // Example Data generation is also an Execute operation, but it is not a synchronisation.
        await SeedActivityAsync(
            ActivityTargetType.DataGeneration,
            ActivityTargetOperationType.Execute,
            ActivityStatus.Complete);

        Assert.That(await _repository.Activity.HasAnyRunProfileExecutionAsync(), Is.False);
    }

    private async Task SeedActivityAsync(
        ActivityTargetType targetType,
        ActivityTargetOperationType operationType,
        ActivityStatus status)
    {
        _dbContext.Activities.Add(new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = targetType,
            TargetOperationType = operationType,
            Status = status,
            TargetName = "Full Import",
            Created = DateTime.UtcNow,
            Executed = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync();
    }
}
