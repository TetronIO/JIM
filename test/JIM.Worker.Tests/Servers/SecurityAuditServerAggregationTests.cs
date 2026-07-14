// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the failed-authentication aggregation-on-write behaviour: a failed API key spray or repeated interactive
/// sign-in failure of any volume must produce a bounded number of Activity rows (one per (API key prefix, client
/// IP, failure reason) per 15-minute UTC window), carrying an attempt counter and first/last-seen timestamps,
/// rather than one row per attempt. Runs against a real (in-memory) JimDbContext through the full JimApplication
/// facade, because the aggregation semantics live in the repository's atomic increment-or-insert behaviour, not
/// just server-level orchestration.
/// </summary>
[TestFixture]
public class SecurityAuditServerAggregationTests
{
    private JimDbContext _dbContext = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        var repository = new PostgresDataRepository(_dbContext);
        _jim = new JimApplication(repository);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _dbContext?.Dispose();
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_SameKeyIpReasonWindow_IncrementsSingleRowAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "repeated attempts matching the same (prefix, IP, reason, window) must aggregate onto one row");
        Assert.That(rows[0].AttemptCount, Is.EqualTo(3));
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_SprayOfNCallsInOneWindow_YieldsOneRowWithAttemptCountEqualToNAsync()
    {
        const int sprayVolume = 25;
        for (var i = 0; i < sprayVolume; i++)
            await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "Invalid API key format", null, "203.0.113.5");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1), "a spray of any volume must produce a bounded (single) row per window bucket");
        Assert.That(rows[0].AttemptCount, Is.EqualTo(sprayVolume));
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_DifferentReason_ProducesDistinctRowAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key is disabled", "jim_ak_1234", "10.0.0.1");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "a different failure reason must not aggregate onto the same row");
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_DifferentClientIp_ProducesDistinctRowAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.2");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "a different client IP must not aggregate onto the same row");
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_DifferentApiKeyPrefix_ProducesDistinctRowAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_5678", "10.0.0.1");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "a different API key prefix must not aggregate onto the same row");
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_DifferentWindow_ProducesDistinctRowAsync()
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.Authentication,
            TargetOperationType = ActivityTargetOperationType.Authenticate,
            TargetName = "API key authentication failed",
            ApiKeyPrefix = "jim_ak_1234",
            ClientIpAddress = "10.0.0.1",
            SecurityEventReason = "API key not found",
            AggregationWindowStart = DateTime.UtcNow.AddMinutes(-30),
            FirstSeen = DateTime.UtcNow.AddMinutes(-30),
            LastSeen = DateTime.UtcNow.AddMinutes(-30),
            AttemptCount = 1,
            InitiatedByType = ActivityInitiatorType.Anonymous,
            InitiatedByName = "Anonymous",
            Status = ActivityStatus.Complete,
            Created = DateTime.UtcNow.AddMinutes(-30)
        };
        _dbContext.Activities.Add(activity);
        await _dbContext.SaveChangesAsync();

        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "a call in a different (current) 15-minute window must not aggregate onto an older window's row");
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_NullPrefixAndIp_NormalisesToEmptyStringForDedupeAsync()
    {
        // Two callers with no known API key prefix and no known client IP (e.g. the bad-format failure path) must
        // still aggregate onto one row, not two: nulls are normalised to "" before matching, because Postgres
        // unique indexes treat NULLs as distinct from one another.
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "Invalid API key format", null, null);
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "Invalid API key format", null, null);

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].AttemptCount, Is.EqualTo(2));
        Assert.That(rows[0].ApiKeyPrefix, Is.EqualTo(string.Empty));
        Assert.That(rows[0].ClientIpAddress, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_FirstSeenFixed_LastSeenAdvancesAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");
        var firstRow = await _dbContext.Activities.SingleAsync(a => a.TargetType == ActivityTargetType.Authentication);
        var firstSeenAtFirstCall = firstRow.FirstSeen;
        var lastSeenAtFirstCall = firstRow.LastSeen;

        await Task.Delay(20); // ensure a measurable time delta between calls
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");

        var secondRow = await _dbContext.Activities.SingleAsync(a => a.TargetType == ActivityTargetType.Authentication);

        Assert.That(secondRow.FirstSeen, Is.EqualTo(firstSeenAtFirstCall), "FirstSeen must not change on subsequent increments");
        Assert.That(secondRow.LastSeen, Is.GreaterThan(lastSeenAtFirstCall!.Value), "LastSeen must advance on every increment");
        Assert.That(secondRow.AttemptCount, Is.EqualTo(2));
    }

    [Test]
    public async Task RecordFailedAuthenticationAsync_CreatesAnonymousAttributedActivityAsync()
    {
        await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1");

        var row = await _dbContext.Activities.SingleAsync(a => a.TargetType == ActivityTargetType.Authentication);

        Assert.That(row.InitiatedByType, Is.EqualTo(ActivityInitiatorType.Anonymous));
        Assert.That(row.InitiatedById, Is.Null);
        Assert.That(row.InitiatedByName, Is.EqualTo("Anonymous"));
        Assert.That(row.TargetName, Is.EqualTo("API key authentication failed"));
        Assert.That(row.Status, Is.EqualTo(ActivityStatus.Complete));
    }

    [Test]
    public async Task RecordInteractiveSignInSucceededAsync_CreatesOneUserAttributedActivityWithNoAggregationFieldsAsync()
    {
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Name = "Display Name" },
            StringValue = "Alice Example"
        });

        await _jim.SecurityAudit.RecordInteractiveSignInSucceededAsync(user, "198.51.100.7");

        var row = await _dbContext.Activities.SingleAsync(a => a.TargetType == ActivityTargetType.Authentication);

        Assert.That(row.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(row.InitiatedById, Is.EqualTo(user.Id));
        Assert.That(row.InitiatedByName, Is.EqualTo("Alice Example"));
        Assert.That(row.ClientIpAddress, Is.EqualTo("198.51.100.7"));
        Assert.That(row.TargetName, Is.EqualTo("Interactive sign-in succeeded"));
        Assert.That(row.AttemptCount, Is.Null, "sign-in success is not an aggregated event");
        Assert.That(row.AggregationWindowStart, Is.Null);
        Assert.That(row.Status, Is.EqualTo(ActivityStatus.Complete));
    }

    [Test]
    public async Task RecordInteractiveSignInSucceededAsync_CalledTwice_CreatesTwoSeparateActivitiesAsync()
    {
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Name = "Display Name" },
            StringValue = "Alice Example"
        });

        await _jim.SecurityAudit.RecordInteractiveSignInSucceededAsync(user, "198.51.100.7");
        await _jim.SecurityAudit.RecordInteractiveSignInSucceededAsync(user, "198.51.100.7");

        var rows = await _dbContext.Activities.Where(a => a.TargetType == ActivityTargetType.Authentication).ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2), "one Activity per session establishment, never aggregated");
    }

    [Test]
    public void RecordFailedAuthenticationAsync_RepositoryThrows_DoesNotPropagateAsync()
    {
        // Dispose the context first so any repository call throws ObjectDisposedException; the audit write must
        // never fail the authentication path it is instrumenting.
        _dbContext.Dispose();

        Assert.DoesNotThrowAsync(async () =>
            await _jim.SecurityAudit.RecordFailedAuthenticationAsync("API key authentication failed", "API key not found", "jim_ak_1234", "10.0.0.1"));
    }

    [Test]
    public void RecordInteractiveSignInSucceededAsync_RepositoryThrows_DoesNotPropagateAsync()
    {
        var user = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Id = 1, Name = "User" }
        };
        user.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = new MetaverseAttribute { Name = "Display Name" },
            StringValue = "Alice Example"
        });

        _dbContext.Dispose();

        Assert.DoesNotThrowAsync(async () =>
            await _jim.SecurityAudit.RecordInteractiveSignInSucceededAsync(user, "198.51.100.7"));
    }
}
