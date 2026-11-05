// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
/// Real-PostgreSQL verification of the Exchange queue's delivery statements (#2021, Phase 5): the claim, its lease and
/// its guards, the attempt write, release, delete, the outlook, retention, and the per-object read the lane decides
/// with.
/// <para>
/// The claim's <c>FOR UPDATE SKIP LOCKED</c>, the guarded raw-SQL attempt write and the queries over Pending Exports
/// all run in PostgreSQL alone; the in-memory twin can only agree with them, never prove them. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExchangeDeliveryClaimDatabaseTests
{
    private static readonly DateTime AsOf = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lease = PendingExchangeChange.ClaimLease;
    private const string Me = "worker-1:aaaa1111";
    private const string Them = "worker-2:bbbb2222";

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private static PostgresDataRepository Repository(JimDbContext context) => new(context);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Exchange delivery tests.");
        _connectionString = connectionString;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task ClaimDueExchangeChangesAsync_TwoClaimantsAtOnce_SplitTheRowsWithNoOverlapAsync()
    {
        var s = await SeedAsync(objectCount: 40);
        foreach (var csoId in s.CsoIds)
            await QueueAsync(s, csoId);

        await using var first = NewContext();
        await using var second = NewContext();
        var claims = await Task.WhenAll(
            Repository(first).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Me, AsOf, Lease, 40),
            Repository(second).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Them, AsOf, Lease, 40));

        var mine = claims[0].Select(c => c.Id).ToHashSet();
        var theirs = claims[1].Select(c => c.Id).ToHashSet();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mine.Overlaps(theirs), Is.False, "no change is claimed twice");
            Assert.That(mine.Count + theirs.Count, Is.EqualTo(40), "every due change is claimed by one of them");
            Assert.That(claims[0], Has.All.Matches<PendingExchangeChange>(c => c.Status == PendingExchangeChangeStatus.Delivering && c.ClaimedBy == Me && c.ClaimedAt == AsOf));
        }
    }

    [Test]
    public async Task ClaimDueExchangeChangesAsync_OnlyDueChanges_AreClaimedAsync()
    {
        var s = await SeedAsync(objectCount: 6);
        var due = await QueueAsync(s, s.CsoIds[0]);
        var dueRetry = await QueueAsync(s, s.CsoIds[1], c => c.NextRetryAt = AsOf.AddMinutes(-1));
        await QueueAsync(s, s.CsoIds[2], c => c.NextRetryAt = AsOf.AddMinutes(1));
        await QueueAsync(s, s.CsoIds[3], c => c.Status = PendingExchangeChangeStatus.Parked);
        await QueueAsync(s, s.CsoIds[4], c => { c.Status = PendingExchangeChangeStatus.Refused; c.FailureMessage = "does not fit"; });
        await QueueAsync(s, s.CsoIds[5], c => { c.Status = PendingExchangeChangeStatus.Delivering; c.ClaimedAt = AsOf.AddSeconds(-30); c.ClaimedBy = Them; });

        await using var claim = NewContext();
        var claimed = await Repository(claim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Me, AsOf, Lease, 100);

        Assert.That(claimed.Select(c => c.Id), Is.EquivalentTo(new[] { due.Id, dueRetry.Id }),
            "a future retry, Parked, Refused and a live claim are all stepped over");
    }

    [Test]
    public async Task ClaimDueExchangeChangesAsync_ClaimWhoseLeaseRanOut_IsClaimedAgainAsync()
    {
        var s = await SeedAsync(objectCount: 1);
        var stranded = await QueueAsync(s, s.CsoIds[0], c => { c.Status = PendingExchangeChangeStatus.Delivering; c.ClaimedAt = AsOf - Lease; c.ClaimedBy = Them; });

        await using var claim = NewContext();
        var claimed = await Repository(claim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Me, AsOf, Lease, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { stranded.Id }));
            Assert.That(claimed[0].ClaimedBy, Is.EqualTo(Me));
        }
    }

    [Test]
    public async Task RecordExchangeChangeAttemptsAsync_ClaimedChange_WritesEveryAttemptColumnAsync()
    {
        var s = await SeedAsync(objectCount: 1);
        await QueueAsync(s, s.CsoIds[0]);
        var claimed = await ClaimAsync(s, Me);
        claimed.RecordFailure(ExchangeChangeFailureKind.Unreachable, "The WinRM client cannot complete the operation.", AsOf);

        await using (var write = NewContext())
            await Repository(write).Sync.RecordExchangeChangeAttemptsAsync([claimed], Me);

        var stored = await ReadAsync(claimed.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored!.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
            Assert.That(stored.FailureMessage, Is.EqualTo("The WinRM client cannot complete the operation."));
            Assert.That(stored.AttemptCount, Is.EqualTo(1));
            Assert.That(stored.LastAttemptedAt, Is.EqualTo(AsOf));
            Assert.That(stored.FirstFailedAt, Is.EqualTo(AsOf));
            Assert.That(stored.NextRetryAt, Is.EqualTo(AsOf.AddMinutes(2)));
            Assert.That(stored.ClaimedAt, Is.Null);
            Assert.That(stored.ClaimedBy, Is.Null);
            Assert.That(stored.RecipientType, Is.EqualTo("Mailbox"), "an attempt never changes what the change is");
        }
    }

    [Test]
    public async Task RecordExchangeChangeAttemptsAsync_ChangeSupersededMeanwhile_KeepsTheNewerStateAsync()
    {
        // A newer declared state lands while the lane is converging the old one. The supersession clears the claim, so
        // the old attempt's outcome must land nowhere: the newer state is due at once, with no failure against it.
        var s = await SeedAsync(objectCount: 1);
        await QueueAsync(s, s.CsoIds[0]);
        var claimed = await ClaimAsync(s, Me);
        await using (var restage = NewContext())
            await Repository(restage).Sync.StageExchangeChangesAsync([NewChange(s, s.CsoIds[0], recipientType: "SharedMailbox")]);

        claimed.RecordFailure(ExchangeChangeFailureKind.Refused, "old state refused", AsOf);
        await using (var write = NewContext())
            await Repository(write).Sync.RecordExchangeChangeAttemptsAsync([claimed], Me);

        var stored = await ReadAsync(claimed.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored!.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(stored.RecipientType, Is.EqualTo("SharedMailbox"));
            Assert.That(stored.FailureKind, Is.Null);
            Assert.That(stored.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task RecordExchangeChangeAttemptsAsync_ChangeReclaimedByAnotherDeliverer_LeavesTheirClaimAloneAsync()
    {
        var s = await SeedAsync(objectCount: 1);
        await QueueAsync(s, s.CsoIds[0]);
        var claimed = await ClaimAsync(s, Me);
        // My lease runs out mid-attempt and another deliverer takes the change.
        await using (var reclaim = NewContext())
            await Repository(reclaim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Them, AsOf + Lease, Lease, 10);

        claimed.RecordFailure(ExchangeChangeFailureKind.Unreachable, null, AsOf);
        await using (var write = NewContext())
            await Repository(write).Sync.RecordExchangeChangeAttemptsAsync([claimed], Me);

        var stored = await ReadAsync(claimed.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored!.Status, Is.EqualTo(PendingExchangeChangeStatus.Delivering));
            Assert.That(stored.ClaimedBy, Is.EqualTo(Them));
            Assert.That(stored.AttemptCount, Is.Zero);
        }
    }

    [Test]
    public async Task DeleteExchangeChangesAsync_RemovesOnlyChangesStillHeldUnderTheClaimAsync()
    {
        var s = await SeedAsync(objectCount: 2);
        await QueueAsync(s, s.CsoIds[0]);
        await QueueAsync(s, s.CsoIds[1]);
        await using var claim = NewContext();
        var claimed = await Repository(claim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, Me, AsOf, Lease, 10);
        var superseded = claimed.Single(c => c.ConnectedSystemObjectId == s.CsoIds[1]);
        await using (var restage = NewContext())
            await Repository(restage).Sync.StageExchangeChangesAsync([NewChange(s, s.CsoIds[1], recipientType: "MailUser")]);

        await using (var delete = NewContext())
        {
            var deleted = await Repository(delete).Sync.DeleteExchangeChangesAsync(claimed.Select(c => c.Id), Me);
            Assert.That(deleted, Is.EqualTo(1));
        }

        await using var verify = NewContext();
        var remaining = await verify.PendingExchangeChanges.AsNoTracking().ToListAsync();
        Assert.That(remaining.Select(c => c.Id), Is.EqualTo(new[] { superseded.Id }), "the superseded change carries newer work");
    }

    [Test]
    public async Task ReleaseExchangeChangeClaimsAsync_ReleasesOnlyTheClaimantsOwnClaimsAsync()
    {
        var s = await SeedAsync(objectCount: 2);
        var mine = await QueueAsync(s, s.CsoIds[0], c => { c.Status = PendingExchangeChangeStatus.Delivering; c.ClaimedAt = AsOf; c.ClaimedBy = Me; });
        var theirs = await QueueAsync(s, s.CsoIds[1], c => { c.Status = PendingExchangeChangeStatus.Delivering; c.ClaimedAt = AsOf; c.ClaimedBy = Them; });

        await using (var release = NewContext())
            Assert.That(await Repository(release).Sync.ReleaseExchangeChangeClaimsAsync([mine.Id, theirs.Id], Me), Is.EqualTo(1));

        var storedMine = await ReadAsync(mine.Id);
        var storedTheirs = await ReadAsync(theirs.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedMine!.Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(storedMine.ClaimedBy, Is.Null);
            Assert.That(storedMine.AttemptCount, Is.Zero, "nothing was tried");
            Assert.That(storedTheirs!.Status, Is.EqualTo(PendingExchangeChangeStatus.Delivering));
            Assert.That(storedTheirs.ClaimedBy, Is.EqualTo(Them));
        }
    }

    [Test]
    public async Task SystemNotManagingExchangeRecipients_IsHeldOutOfTheDueListTheOutlookAndTheClaimAsync()
    {
        // Turning management off holds delivery: the changes are kept, unattempted, and nothing counts them as due, or
        // the service would see permanent work and run a lane on every poll.
        var s = await SeedAsync(objectCount: 2, managing: false);
        await QueueAsync(s, s.CsoIds[0]);
        await QueueAsync(s, s.CsoIds[1], c => c.NextRetryAt = AsOf.AddMinutes(5));

        await using var read = NewContext();
        var outlook = await Repository(read).Sync.GetExchangeQueueDeliveryOutlookAsync(AsOf, Lease);
        var dueSystems = await Repository(read).Sync.GetConnectedSystemIdsWithDueExchangeChangesAsync(AsOf, Lease);
        await using var claim = NewContext();
        var claimed = await Repository(claim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, "worker-a", AsOf, Lease, 10);
        var waiting = await Repository(claim).Sync.CountWaitingExchangeChangesAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outlook.DueCount, Is.Zero);
            Assert.That(outlook.RetryingCount, Is.Zero);
            Assert.That(dueSystems, Is.Empty);
            Assert.That(claimed, Is.Empty);
            Assert.That(waiting, Is.EqualTo(2), "held changes are kept, and still wait");
        }
    }

    [Test]
    public async Task OutlookAndDueSystems_CountWhatALaneWouldAttemptAsync()
    {
        var s = await SeedAsync(objectCount: 4);
        await QueueAsync(s, s.CsoIds[0]);
        await QueueAsync(s, s.CsoIds[1], c => c.NextRetryAt = AsOf.AddMinutes(5));
        await QueueAsync(s, s.CsoIds[2], c => c.NextRetryAt = AsOf.AddMinutes(9));
        await QueueAsync(s, s.CsoIds[3], c => c.Status = PendingExchangeChangeStatus.Parked);

        await using var read = NewContext();
        var outlook = await Repository(read).Sync.GetExchangeQueueDeliveryOutlookAsync(AsOf, Lease);
        var dueSystems = await Repository(read).Sync.GetConnectedSystemIdsWithDueExchangeChangesAsync(AsOf, Lease);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outlook.DueCount, Is.EqualTo(1));
            Assert.That(outlook.RetryingCount, Is.EqualTo(2));
            Assert.That(outlook.NextAttemptAt, Is.EqualTo(AsOf.AddMinutes(5)));
            Assert.That(dueSystems, Is.EqualTo(new[] { s.SystemId }));
        }
    }

    [Test]
    public async Task DeleteTerminalExchangeChangesAsync_RemovesOnlyOldParkedChangesAsync()
    {
        var s = await SeedAsync(objectCount: 5);
        var oldParked = await QueueAsync(s, s.CsoIds[0], c => { c.Status = PendingExchangeChangeStatus.Parked; c.LastAttemptedAt = AsOf.AddDays(-100); });
        await QueueAsync(s, s.CsoIds[1], c => { c.Status = PendingExchangeChangeStatus.Parked; c.LastAttemptedAt = AsOf.AddDays(-1); });
        await QueueAsync(s, s.CsoIds[2], c => { c.Status = PendingExchangeChangeStatus.Refused; c.FailureMessage = "does not fit"; c.CreatedAt = AsOf.AddDays(-100); });
        await QueueAsync(s, s.CsoIds[3], c => { c.Status = PendingExchangeChangeStatus.Delivering; c.ClaimedAt = AsOf.AddDays(-100); c.ClaimedBy = Them; c.CreatedAt = AsOf.AddDays(-100); });
        await QueueAsync(s, s.CsoIds[4], c => c.CreatedAt = AsOf.AddDays(-100));

        await using (var delete = NewContext())
            Assert.That(await Repository(delete).Sync.DeleteTerminalExchangeChangesAsync(AsOf.AddDays(-90), 100), Is.EqualTo(1));

        await using var verify = NewContext();
        var remaining = await verify.PendingExchangeChanges.AsNoTracking().Select(c => c.Id).ToListAsync();
        Assert.That(remaining, Does.Not.Contain(oldParked.Id).And.Count.EqualTo(4),
            "a recent park, a refusal, a delivery in flight and pending work are all kept");
    }

    [Test]
    public async Task GetExchangeDeliveryObjectsAsync_ReadsTheIdentifiersAndTheDeclaredStateNowAsync()
    {
        var s = await SeedAsync(objectCount: 2);
        await using (var write = NewContext())
        {
            var held = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = s.SystemId,
                ConnectedSystemObjectId = s.CsoIds[0],
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Exported,
                CreatedAt = AsOf,
                AttributeValueChanges =
                [
                    ExportChange(s.RecipientTypeAttributeId, "Mailbox", PendingExportAttributeChangeStatus.ExportedPendingConfirmation, awaitsExchange: true),
                    // Not held for Exchange: a change no longer awaiting confirmation, and a directory change.
                    ExportChange(s.AliasAttributeId, "ada", PendingExportAttributeChangeStatus.ExportedNotConfirmed, awaitsExchange: true),
                    ExportChange(s.DistinguishedNameAttributeId, "CN=Ada,DC=panoply,DC=local", PendingExportAttributeChangeStatus.ExportedPendingConfirmation, awaitsExchange: false)
                ]
            };
            await Repository(write).Sync.CreatePendingExportsAsync([held]);
        }

        await using var read = NewContext();
        var objects = await Repository(read).Sync.GetExchangeDeliveryObjectsAsync([s.CsoIds[0], s.CsoIds[1], Guid.NewGuid()]);

        var ada = objects.Single(o => o.ConnectedSystemObjectId == s.CsoIds[0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objects, Has.Count.EqualTo(2), "an id with no object is absent");
            Assert.That(ada.ObjectType, Is.EqualTo("user"));
            Assert.That(ada.ObjectGuid, Is.EqualTo(s.ObjectGuids[0]));
            Assert.That(ada.DistinguishedName, Is.EqualTo("CN=Object 0,OU=Users,DC=panoply,DC=local"));
            Assert.That(ada.ImportedValues, Is.EquivalentTo(new Dictionary<string, string?>
            {
                [ExchangeRecipientAttributes.RecipientType] = "None",
                [ExchangeRecipientAttributes.Alias] = "object0"
            }));
            Assert.That(ada.HeldChanges, Is.EqualTo(new[]
            {
                new ExchangeHeldChange(ExchangeRecipientAttributes.RecipientType, PendingExportAttributeChangeType.Update, "Mailbox")
            }));
            Assert.That(objects.Single(o => o.ConnectedSystemObjectId == s.CsoIds[1]).HeldChanges, Is.Empty);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private sealed record Seeded(int SystemId, int SyncRuleId, int RecipientTypeAttributeId, int AliasAttributeId, int DistinguishedNameAttributeId,
        List<Guid> CsoIds, List<Guid> ObjectGuids);

    private static PendingExchangeChange NewChange(Seeded s, Guid csoId, string recipientType = "Mailbox") => new()
    {
        ConnectedSystemId = s.SystemId,
        ConnectedSystemObjectId = csoId,
        SyncRuleId = s.SyncRuleId,
        RecipientType = recipientType,
        DomainController = "dc1.panoply.local",
        CreatedAt = AsOf.AddMinutes(-10),
        ActivityId = Guid.NewGuid()
    };

    /// <summary>
    /// A change on the queue for <paramref name="csoId"/>, staged as export staging would, then left as
    /// <paramref name="leftAs"/> says, as an earlier attempt or an administrator would leave it.
    /// </summary>
    private async Task<PendingExchangeChange> QueueAsync(Seeded s, Guid csoId, Action<PendingExchangeChange>? leftAs = null)
    {
        var change = NewChange(s, csoId);
        await using (var stage = NewContext())
            await Repository(stage).Sync.StageExchangeChangesAsync([change]);

        if (leftAs == null)
            return change;

        await using var adjust = NewContext();
        var row = await adjust.PendingExchangeChanges.SingleAsync(r => r.Id == change.Id);
        leftAs(row);
        await adjust.SaveChangesAsync();
        return row;
    }

    private async Task<PendingExchangeChange> ClaimAsync(Seeded s, string claimant)
    {
        await using var claim = NewContext();
        return (await Repository(claim).Sync.ClaimDueExchangeChangesAsync(s.SystemId, claimant, AsOf, Lease, 10)).Single();
    }

    private async Task<PendingExchangeChange?> ReadAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.PendingExchangeChanges.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
    }

    private static PendingExportAttributeValueChange ExportChange(int attributeId, string value, PendingExportAttributeChangeStatus status, bool awaitsExchange) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        ChangeType = PendingExportAttributeChangeType.Update,
        StringValue = value,
        Status = status,
        ExportAttemptCount = 1,
        AwaitsExchange = awaitsExchange
    };

    private async Task<Seeded> SeedAsync(int objectCount, bool managing = true)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Panoply AD Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Panoply AD", ConnectorDefinition = connectorDefinition, ManagesExchangeRecipients = managing };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mvType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = false };
        seed.AddRange(connectorDefinition, system, csType, mvType);
        await seed.SaveChangesAsync();

        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool connectorDefined = false) => new()
        {
            Name = name, Type = type, AttributePlurality = AttributePlurality.SingleValued, Selected = true,
            IsConnectorDefined = connectorDefined, ConnectedSystemObjectType = csType
        };
        var objectGuid = Attribute("objectGUID", AttributeDataType.Guid);
        var distinguishedName = Attribute("distinguishedName", AttributeDataType.Text);
        var recipientType = Attribute(ExchangeRecipientAttributes.RecipientType, AttributeDataType.Text, connectorDefined: true);
        var alias = Attribute(ExchangeRecipientAttributes.Alias, AttributeDataType.Text, connectorDefined: true);
        seed.AddRange(objectGuid, distinguishedName, recipientType, alias);
        await seed.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = "Provision Users", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvType.Id, ProvisionToConnectedSystem = true
        };
        seed.Add(syncRule);

        var csoIds = new List<Guid>();
        var guids = new List<Guid>();
        for (var i = 0; i < objectCount; i++)
        {
            var guid = Guid.NewGuid();
            var cso = new ConnectedSystemObject
            {
                TypeId = csType.Id,
                ConnectedSystemId = system.Id,
                Status = ConnectedSystemObjectStatus.Normal,
                ExternalIdAttributeId = objectGuid.Id,
                SecondaryExternalIdAttributeId = distinguishedName.Id
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = objectGuid.Id, GuidValue = guid });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = distinguishedName.Id, StringValue = $"CN=Object {i},OU=Users,DC=panoply,DC=local" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = recipientType.Id, StringValue = "None" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = alias.Id, StringValue = $"object{i}" });
            seed.Add(cso);
            await seed.SaveChangesAsync();
            csoIds.Add(cso.Id);
            guids.Add(guid);
        }

        await seed.SaveChangesAsync();
        return new Seeded(system.Id, syncRule.Id, recipientType.Id, alias.Id, distinguishedName.Id, csoIds, guids);
    }
}
