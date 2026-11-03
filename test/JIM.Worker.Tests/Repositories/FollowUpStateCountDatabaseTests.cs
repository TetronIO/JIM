// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// A run's follow-up summary (#2021, Phase 6) against real PostgreSQL.
/// <para>
/// The summary decides each follow-up's state in SQL, so that a run with as many follow-ups as accounts is counted in
/// one read; the item page, the causality views and the REST follow-ups decide it in memory, from the same queue rows
/// and Activities. Two readings of one fact drift apart silently, so every state either reading can reach is seeded
/// here, each under a run of its own, and the two are held to the same answer.
/// </para>
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as the other <c>RequiresPostgres</c> fixtures are.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class FollowUpStateCountDatabaseTests
{
    private static readonly DateTime At = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private int _systemId;
    private int _mvTypeId;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL follow-up summary tests.");
        _connectionString = connectionString;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Corporate AD Connector", BuiltIn = true, SupportsPasswordSet = true };
        var system = new ConnectedSystem { Name = "Corporate AD", ConnectorDefinition = connectorDefinition };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = false };
        seed.AddRange(connectorDefinition, system, mvType);
        await seed.SaveChangesAsync();
        _systemId = system.Id;
        _mvTypeId = mvType.Id;
    }

    /// <summary>
    /// Every state: the SQL count and the in-memory follow-up agree on each one, and each is what it should be.
    /// </summary>
    [Test]
    public async Task GetFollowUpStateCountsAsync_EveryState_AgreesWithTheFollowUpsOwnReadingAsync()
    {
        var cases = new List<(string Name, string Expected, Func<Guid, Task<Activity>> Seed)>
        {
            // ---- Exchange: on the queue ----
            ("exchange queued", "Queued", item => ExchangeAsync(item, row: r => { })),
            ("exchange delivering", "Delivering", item => ExchangeAsync(item, row: r => r.Status = PendingExchangeChangeStatus.Delivering)),
            ("exchange retrying", "Retrying", item => ExchangeAsync(item, row: r => { r.LastAttemptedAt = At.AddMinutes(1); r.AttemptCount = 1; })),
            ("exchange parked row", "Parked", item => ExchangeAsync(item, row: r => r.Status = PendingExchangeChangeStatus.Parked)),
            ("exchange cancelled row", "Cancelled", item => ExchangeAsync(item, row: r => r.Status = PendingExchangeChangeStatus.Cancelled)),
            ("exchange refused row", "Refused", item => ExchangeAsync(item, ActivityStatus.CompleteWithError, r => r.Status = PendingExchangeChangeStatus.Refused)),
            ("exchange not made row", "NotMade", item => ExchangeAsync(item, ActivityStatus.CompleteWithWarning, r => r.Status = PendingExchangeChangeStatus.Refused)),
            // ---- Exchange: off the queue ----
            ("exchange delivered", "Delivered", item => ExchangeAsync(item, events:
                [(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.CompleteWithError), (ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete)])),
            ("exchange delivered then retried", "Delivered", item => ExchangeAsync(item, events:
                [(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.Complete), (ActivityTargetOperationType.RetryExchangeRecipient, ActivityStatus.Complete)])),
            ("exchange withdrawn", "Withdrawn", item => ExchangeAsync(item, events: [(ActivityTargetOperationType.WithdrawExchangeRecipient, ActivityStatus.Complete)])),
            ("exchange replaced", "Replaced", item => ExchangeAsync(item, events: [(ActivityTargetOperationType.ReplaceExchangeRecipient, ActivityStatus.Complete)])),
            ("exchange cancelled", "Cancelled", item => ExchangeAsync(item, events: [(ActivityTargetOperationType.CancelExchangeRecipient, ActivityStatus.Complete)])),
            ("exchange parked, pruned", "Parked", item => ExchangeAsync(item, events: [(ActivityTargetOperationType.SetExchangeRecipient, ActivityStatus.CompleteWithError)])),
            ("exchange refused, nothing after", "Refused", item => ExchangeAsync(item, ActivityStatus.CompleteWithError)),
            ("exchange not made, nothing after", "NotMade", item => ExchangeAsync(item, ActivityStatus.CompleteWithWarning)),
            ("exchange gone, nothing after", "Withdrawn", item => ExchangeAsync(item)),
            // ---- Initial Passwords ----
            ("password queued", "Queued", item => PasswordAsync(item, row: r => { })),
            ("password delivering", "Delivering", item => PasswordAsync(item, row: r => r.Status = PendingPasswordChangeStatus.Delivering)),
            ("password retrying", "Retrying", item => PasswordAsync(item, row: r => { r.LastAttemptedAt = At.AddMinutes(1); r.AttemptCount = 1; })),
            ("password parked row", "Parked", item => PasswordAsync(item, row: r => r.Status = PendingPasswordChangeStatus.Parked)),
            ("password expired", "Expired", item => PasswordAsync(item, row: r => r.Status = PendingPasswordChangeStatus.Expired)),
            ("password cancelled", "Cancelled", item => PasswordAsync(item, row: r => r.Status = PendingPasswordChangeStatus.Cancelled)),
            ("password set", "Set", item => PasswordAsync(item, events: [ActivityStatus.Complete])),
            ("password set with a warning", "Set", item => PasswordAsync(item, events: [ActivityStatus.CompleteWithWarning])),
            ("password parked, pruned", "Parked", item => PasswordAsync(item, events: [ActivityStatus.CompleteWithError])),
            ("password carried", "Carried", item => PasswordAsync(item))
        };

        var runs = new Dictionary<Guid, (string Name, string Expected, Guid ItemId)>();
        foreach (var (name, expected, seed) in cases)
        {
            var (runId, itemId) = await SeedRunAsync();
            await seed(itemId);
            runs[runId] = (name, expected, itemId);
        }

        await using var read = NewContext();
        var repository = new PostgresDataRepository(read);
        var counts = await repository.Activity.GetFollowUpStateCountsAsync(runs.Keys.ToList());
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var followUps = await jim.Activities.GetExecutionItemFollowUpsAsync(runs.Values.Select(r => r.ItemId).ToList());

        using (Assert.EnterMultipleScope())
        {
            foreach (var (runId, (name, expected, itemId)) in runs)
            {
                var counted = counts.Where(c => c.RunActivityId == runId).ToList();
                var followUp = followUps[itemId].Single();
                var inMemory = followUp.TargetType == ActivityTargetType.ExchangeRecipient
                    ? followUp.ExchangeOutcome?.State.ToString()
                    : followUp.PasswordOutcomes?.Targets.FirstOrDefault()?.State.ToString() ?? "Carried";

                Assert.That(counted, Has.Count.EqualTo(1), $"{name}: one state counted");
                Assert.That(counted.FirstOrDefault()?.State, Is.EqualTo(expected), $"{name}: the summary's state");
                Assert.That(counted.FirstOrDefault()?.Kind, Is.EqualTo(followUp.TargetType), $"{name}: the summary's kind");
                Assert.That(inMemory, Is.EqualTo(expected), $"{name}: the follow-up's own state");
            }
        }
    }

    /// <summary>
    /// Follow-ups are counted per run, kind and state; a run with none contributes nothing, and other kinds of
    /// Activity under the run's items are not follow-ups the summary counts.
    /// </summary>
    [Test]
    public async Task GetFollowUpStateCountsAsync_ARunsFollowUps_AreCountedByKindAndStateAsync()
    {
        var (runId, firstItem) = await SeedRunAsync();
        var secondItem = await SeedItemAsync(runId);
        await ExchangeAsync(firstItem, row: r => { });
        await ExchangeAsync(secondItem, row: r => { });
        await PasswordAsync(firstItem, events: [ActivityStatus.Complete]);
        await using (var seed = NewContext())
        {
            seed.Activities.Add(NewActivity(ActivityTargetType.ConnectedSystem, ActivityStatus.Complete, firstItem));
            await seed.SaveChangesAsync();
        }
        var (quietRun, _) = await SeedRunAsync();

        await using var read = NewContext();
        var counts = await new PostgresDataRepository(read).Activity.GetFollowUpStateCountsAsync([runId, quietRun]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Select(c => (c.RunActivityId, c.Kind, c.State, c.Count)), Is.EquivalentTo(new[]
            {
                (runId, ActivityTargetType.ExchangeRecipient, "Queued", 2),
                (runId, ActivityTargetType.PasswordSynchronisation, "Set", 1)
            }));
            Assert.That(await new PostgresDataRepository(read).Activity.GetFollowUpStateCountsAsync([]), Is.Empty);
        }
    }

    // ---- Seeding --------------------------------------------------------------------------------------------------

    private async Task<(Guid RunId, Guid ItemId)> SeedRunAsync()
    {
        var run = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, ActivityStatus.Complete, item: null);
        await using (var seed = NewContext())
        {
            seed.Activities.Add(run);
            await seed.SaveChangesAsync();
        }

        return (run.Id, await SeedItemAsync(run.Id));
    }

    private async Task<Guid> SeedItemAsync(Guid runId)
    {
        await using var seed = NewContext();
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = runId, ObjectChangeType = ObjectChangeType.Exported };
        seed.ActivityRunProfileExecutionItems.Add(item);
        await seed.SaveChangesAsync();
        return item.Id;
    }

    private async Task<Activity> ExchangeAsync(Guid itemId, ActivityStatus status = ActivityStatus.Complete, Action<PendingExchangeChange>? row = null,
        IReadOnlyList<(ActivityTargetOperationType Operation, ActivityStatus Status)>? events = null)
    {
        var change = NewActivity(ActivityTargetType.ExchangeRecipient, status, itemId);
        change.TargetContext = ExchangeRecipientTypes.Mailbox;
        await using var seed = NewContext();
        seed.Activities.Add(change);
        AddEvents(seed, change, (events ?? []).Select(e => (e.Operation, e.Status)), connectedSystemId: null);
        if (row != null)
        {
            // No account: the queue allows any number of those, which spares seeding one per case.
            var queued = new PendingExchangeChange
            {
                ConnectedSystemId = _systemId, RecipientType = ExchangeRecipientTypes.Mailbox, CreatedAt = At, ActivityId = change.Id
            };
            row(queued);
            seed.PendingExchangeChanges.Add(queued);
        }

        await seed.SaveChangesAsync();
        return change;
    }

    private async Task<Activity> PasswordAsync(Guid itemId, Action<PendingPasswordChange>? row = null, IReadOnlyList<ActivityStatus>? events = null)
    {
        var change = NewActivity(ActivityTargetType.PasswordSynchronisation, ActivityStatus.Complete, itemId);
        change.TargetOperationType = ActivityTargetOperationType.SetPassword;
        change.TargetContext = nameof(PendingPasswordChangeOrigin.Provisioned);
        change.ConnectedSystemId = _systemId;
        await using var seed = NewContext();
        // One identity per change: the queue holds one row per identity and system.
        var identity = new MetaverseObject { Type = await seed.MetaverseObjectTypes.SingleAsync(t => t.Id == _mvTypeId) };
        seed.Add(identity);
        seed.Activities.Add(change);
        AddEvents(seed, change, (events ?? []).Select(e => (ActivityTargetOperationType.SetPassword, e)), _systemId);
        if (row != null)
        {
            await seed.SaveChangesAsync();
            var queued = new PendingPasswordChange
            {
                MetaverseObjectId = identity.Id, ConnectedSystemId = _systemId, CreatedAt = At, ExpiresAt = At.AddDays(7), ActivityId = change.Id,
                // An Initial Password's own row: never held, whatever the system's Password Synchronisation setting.
                Origin = PendingPasswordChangeOrigin.Provisioned
            };
            row(queued);
            seed.PendingPasswordChanges.Add(queued);
        }

        await seed.SaveChangesAsync();
        return change;
    }

    private static void AddEvents(JimDbContext seed, Activity change, IEnumerable<(ActivityTargetOperationType Operation, ActivityStatus Status)> events,
        int? connectedSystemId)
    {
        var minute = 1;
        foreach (var (operation, status) in events)
        {
            var outcome = NewActivity(change.TargetType, status, item: null);
            outcome.ParentActivityId = change.Id;
            outcome.TargetOperationType = operation;
            outcome.ConnectedSystemId = connectedSystemId;
            outcome.Created = At.AddMinutes(minute++);
            seed.Activities.Add(outcome);
        }
    }

    private static Activity NewActivity(ActivityTargetType targetType, ActivityStatus status, Guid? item) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = targetType,
        TargetOperationType = targetType == ActivityTargetType.ExchangeRecipient
            ? ActivityTargetOperationType.SetExchangeRecipient
            : ActivityTargetOperationType.Execute,
        TargetName = "Corporate AD",
        ParentRunProfileExecutionItemId = item,
        Status = status,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = At,
        Executed = At
    };
}
