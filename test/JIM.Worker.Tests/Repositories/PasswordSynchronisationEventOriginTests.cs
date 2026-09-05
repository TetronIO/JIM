// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// How a password change's origin reaches the person's password history (#1635). The history is read from
/// Activities, which carry the origin as the parent Activity's <see cref="Activity.TargetContext"/>; this pins the
/// projection of that text back into <see cref="PendingPasswordChangeOrigin"/>, including the Activities written
/// before origins existed, which carry nothing there and must project to null rather than to a guess.
/// <para>
/// The in-memory provider is enough here: the projection is a per-row expression over one column, and what is
/// under test is its mapping, not the two-query join that <c>PasswordSynchronisationEventDatabaseTests</c> covers
/// against a real PostgreSQL.
/// </para>
/// </summary>
[TestFixture]
public class PasswordSynchronisationEventOriginTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;
    private readonly Guid _metaverseObjectId = Guid.NewGuid();

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

    private async Task<Guid> SeedChangeAsync(string? targetContext, DateTime created)
    {
        var change = new Activity
        {
            Id = Guid.NewGuid(),
            Created = created,
            Executed = created,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            TargetName = "Ada Lovelace",
            TargetContext = targetContext,
            MetaverseObjectId = _metaverseObjectId,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = "Grace Hopper",
            InitiatedById = Guid.NewGuid(),
            Status = ActivityStatus.Complete,
            Message = "Password set requested for 1 account: Corporate AD."
        };
        _dbContext.Activities.Add(change);
        await _dbContext.SaveChangesAsync();
        return change.Id;
    }

    [TestCase("Explicit", PendingPasswordChangeOrigin.Explicit)]
    [TestCase("Propagated", PendingPasswordChangeOrigin.Propagated)]
    public async Task GetPasswordSynchronisationEventsAsync_ChangeWithAnOrigin_ProjectsItAsync(string targetContext, PendingPasswordChangeOrigin expected)
    {
        await SeedChangeAsync(targetContext, new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc));

        var events = await _repository.Activity.GetPasswordSynchronisationEventsAsync(_metaverseObjectId, 10);

        Assert.That(events.Single().Origin, Is.EqualTo(expected));
    }

    /// <summary>
    /// An Activity from before the origin was recorded says nothing about which way the change was aimed, and the
    /// panel draws no kind chip for it. Defaulting it to Propagated would relabel every historic administrator
    /// reset as a propagated change, which is the more misleading of the two possible mistakes.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("Corporate AD")]
    public async Task GetPasswordSynchronisationEventsAsync_ChangeWithoutARecognisedOrigin_ProjectsNullAsync(string? targetContext)
    {
        await SeedChangeAsync(targetContext, new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc));

        var events = await _repository.Activity.GetPasswordSynchronisationEventsAsync(_metaverseObjectId, 10);

        Assert.That(events.Single().Origin, Is.Null);
    }
}
