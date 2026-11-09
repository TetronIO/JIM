// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2705: a Delta Import looks up only the External IDs each page returns, instead of loading every object the Connected
/// System holds before it reads anything (3.6 s on every near-empty Delta Import at 525,000 objects, and the whole lookup
/// held in memory for the run). Matching must come out exactly as it did: an object JIM holds is updated, never created
/// again. A Full Import, and a Delta Import whose Connector read every object, still load every object once, since they
/// look up every object anyway.
/// </summary>
[TestFixture]
public class DeltaImportExternalIdLookupTests : WorkflowTestBase
{
    /// <summary>
    /// Records what each import asks the repository for, so a test can see whether every External ID was loaded.
    /// </summary>
    private sealed class LookupSpySyncRepository : JIM.InMemoryData.SyncRepository
    {
        public int WholeLookupLoads { get; private set; }
        public List<List<CsoExternalIdProbe>> PageLookups { get; } = new();
        public bool FoldsCaseLikeIdentifierKey { get; set; } = true;

        public override Task<Dictionary<string, CsoImportStateLookupEntry>> GetAllCsoImportStateLookupAsync(int connectedSystemId)
        {
            WholeLookupLoads++;
            return base.GetAllCsoImportStateLookupAsync(connectedSystemId);
        }

        public override Task<Dictionary<string, CsoImportStateLookupEntry>> GetCsoImportStateLookupByExternalIdsAsync(int connectedSystemId, IReadOnlyCollection<CsoExternalIdProbe> probes)
        {
            PageLookups.Add(probes.ToList());
            return base.GetCsoImportStateLookupByExternalIdsAsync(connectedSystemId, probes);
        }

        public override Task<bool> DoesDatabaseFoldCaseLikeIdentifierKeyAsync() => Task.FromResult(FoldsCaseLikeIdentifierKey);
    }

    private LookupSpySyncRepository _spy = null!;

    [SetUp]
    public void SetUpSpy()
    {
        // Replaced before any seeding, so every helper seeds the repository the import reads.
        _spy = new LookupSpySyncRepository();
        _spy.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _spy;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    [Test]
    public async Task DeltaImport_ObjectJimHolds_UpdatesItWithoutLoadingEveryExternalIdAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 5);
        var changed = held[2];

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, changed.ExternalId, "Changed", ObjectChangeType.Updated)]],
            ConnectedSystemRunType.DeltaImport);

        var stored = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, changed.Cso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(5), "Updated, not created again");
            Assert.That(DisplayNames(stored!), Is.EqualTo(new[] { "Changed" }));
            Assert.That(_spy.WholeLookupLoads, Is.Zero, "Every External ID was loaded for a one-object Delta Import");
            Assert.That(_spy.PageLookups.SelectMany(p => p).Select(p => p.ValueKey), Is.EqualTo(new[] { changed.ExternalId.ToString() }),
                "Only the External ID the page returned is looked up");
        }
    }

    [Test]
    public async Task DeltaImport_NewObject_CreatesItWithoutLoadingEveryExternalIdAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        SeedCsos(connectedSystem, type, 5);

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, Guid.NewGuid(), "New", ObjectChangeType.Added)]],
            ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(6));
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Added), Is.EqualTo(1));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    /// <summary>
    /// An object created during a run is saved only after its last page, so a later page's lookup cannot find it in the
    /// database, as the whole lookup, loaded before the first page, could not either. The later copy takes the earlier
    /// one's place (#2074) rather than being created a second time.
    /// </summary>
    [Test]
    public async Task DeltaImport_ObjectCreatedOnOnePageAndReturnedOnTheNext_CreatesItOnceAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        SeedCsos(connectedSystem, type, 5);
        var externalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(type, externalId, "First", ObjectChangeType.Added)], [ImportObject(type, externalId, "Second", ObjectChangeType.Updated)]],
            ConnectedSystemRunType.DeltaImport);

        var created = (await SyncRepo.GetConnectedSystemObjectsAsync(connectedSystem.Id, 1, 10)).Results
            .Where(cso => cso.AttributeValues.Any(av => av.GuidValue == externalId))
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(created, Has.Count.EqualTo(1), "Created once");
            Assert.That(DisplayNames(created.Single()), Is.EqualTo(new[] { "Second" }));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
            Assert.That(_spy.PageLookups, Has.Count.EqualTo(2), "Each page looks up its own External IDs");
        }
    }

    /// <summary>
    /// An object JIM provisioned has no External ID until the import that confirms it: it is found by its Secondary External
    /// ID (a distinguished name), and must not be created a second time.
    /// </summary>
    [Test]
    public async Task DeltaImport_ProvisionedObjectConfirmedBySecondaryExternalId_IsNotCreatedAgainAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DistinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        SeedCsos(connectedSystem, type, 3);
        var dn = type.Attributes.Single(a => a.IsSecondaryExternalId);
        var provisioned = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = type.Attributes.Single(a => a.IsExternalId).Id,
            SecondaryExternalIdAttributeId = dn.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            Created = DateTime.UtcNow
        };
        provisioned.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = dn.Id, Attribute = dn, StringValue = "CN=Joe Bloggs,OU=People,DC=Glitterband", ConnectedSystemObject = provisioned
        });
        SyncRepo.SeedConnectedSystemObject(provisioned);
        var assigned = Guid.NewGuid();

        var importObject = ImportObject(type, assigned, "Joe Bloggs", ObjectChangeType.Added);
        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
        {
            Name = "DistinguishedName", Type = AttributeDataType.Text, StringValues = ["cn=joe bloggs,ou=people,dc=glitterband"]
        });
        var activity = await RunImportAsync(connectedSystem, [[importObject]], ConnectedSystemRunType.DeltaImport);

        var stored = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, provisioned.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(4), "Confirmed, not created again");
            Assert.That(stored!.AttributeValues.Any(av => av.GuidValue == assigned), Is.True, "The provisioned object takes its External ID");
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    [Test]
    public async Task DeltaImport_DeleteReportedByTheConnector_MarksOnlyThatObjectDeletedAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 5);
        var deleted = held[1];

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, deleted.ExternalId, changeType: ObjectChangeType.Deleted)]],
            ConnectedSystemRunType.DeltaImport);

        var obsolete = new List<Guid>();
        foreach (var cso in held)
        {
            if ((await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, cso.Cso.Id))!.Status == ConnectedSystemObjectStatus.Obsolete)
                obsolete.Add(cso.Cso.Id);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(obsolete, Is.EqualTo(new[] { deleted.Cso.Id }));
            Assert.That(activity.RunProfileExecutionItems.Single(i => i.ObjectChangeType == ObjectChangeType.Deleted).ConnectedSystemObjectId,
                Is.EqualTo(deleted.Cso.Id));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    /// <summary>
    /// A Connector that falls back to reading every object returns every object JIM holds, so the run looks up every one
    /// of them: one read of them all is the cheaper way, as on a Full Import.
    /// </summary>
    [Test]
    public async Task DeltaImport_ConnectorReadEveryObject_LoadsEveryExternalIdOnceAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 4);

        var activity = await RunImportAsync(connectedSystem,
            [held.Take(2).Select(h => ImportObject(type, h.ExternalId, "Same")).ToList(), held.Skip(2).Select(h => ImportObject(type, h.ExternalId, "Same")).ToList()],
            ConnectedSystemRunType.DeltaImport, completeReadByPage: [true, true]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(4));
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
            Assert.That(_spy.PageLookups, Is.Empty);
        }
    }

    [Test]
    public async Task FullImport_LoadsEveryExternalIdOnceAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 3);

        var activity = await RunImportAsync(connectedSystem, [held.Select(h => ImportObject(type, h.ExternalId, "Same")).ToList()]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(3));
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
            Assert.That(_spy.PageLookups, Is.Empty);
        }
    }

    /// <summary>
    /// Ignoring case, only JIM's own folding can say whether a value outside ASCII names an object JIM holds: the
    /// database's lower() need not fold it the same way. The run loads every External ID rather than risk a miss, and
    /// matches the object JIM holds.
    /// </summary>
    [Test]
    public async Task DeltaImport_ExternalIdIgnoringCaseOutsideAscii_LoadsEveryExternalIdAndMatchesTheHeldObjectAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("HR");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "person",
        [
            new() { Name = "PersonId", Type = AttributeDataType.Text, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        var held = SeedTextCso(connectedSystem, type, "ÉMILE");

        var activity = await RunImportAsync(connectedSystem, [[TextImportObject(type, "émile", "Émile Zola")]], ConnectedSystemRunType.DeltaImport);

        var stored = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, held.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1), "Matched, not created again");
            Assert.That(DisplayNames(stored!), Is.EqualTo(new[] { "Émile Zola" }));
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// On a database whose lower() does not fold as JIM does (a C collation folds ASCII only), a value ignoring case that
    /// holds an i or a k could be stored spelt with the dotted capital I or the Kelvin sign, which that database would not
    /// find, so the run loads every External ID.
    /// </summary>
    [Test]
    public async Task DeltaImport_DatabaseFoldsCaseUnlikeJim_LoadsEveryExternalIdAndMatchesTheHeldObjectAsync()
    {
        _spy.FoldsCaseLikeIdentifierKey = false;
        var connectedSystem = await CreateConnectedSystemAsync("HR");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "person",
        [
            new() { Name = "PersonId", Type = AttributeDataType.Text, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        var held = SeedTextCso(connectedSystem, type, "JKing");

        var activity = await RunImportAsync(connectedSystem, [[TextImportObject(type, "jking", "Joe King")]], ConnectedSystemRunType.DeltaImport);

        var stored = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, held.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(1));
            Assert.That(DisplayNames(stored!), Is.EqualTo(new[] { "Joe King" }));
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
            Assert.That(_spy.PageLookups, Is.Empty);
        }
    }

    /// <summary>
    /// A GUID holds no letter a character outside ASCII folds to, and every database folds ASCII, so a directory's
    /// Delta Import still looks up page by page on a database that does not fold as JIM does.
    /// </summary>
    [Test]
    public async Task DeltaImport_GuidExternalIdOnADatabaseFoldingCaseUnlikeJim_StillLooksUpPageByPageAsync()
    {
        _spy.FoldsCaseLikeIdentifierKey = false;
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 3);

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(type, held[0].ExternalId, "Changed", ObjectChangeType.Updated)]],
            ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(3));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    /// <summary>
    /// A Delta Import that returns most of what JIM holds without saying it read everything (a bulk change at the source)
    /// stops looking up page by page once that would cost more than loading every External ID.
    /// </summary>
    [Test]
    public async Task DeltaImport_LooksUpMoreThanHalfTheObjectsJimHolds_LoadsEveryExternalIdForTheRestOfTheRunAsync()
    {
        var (connectedSystem, type) = await ArrangeAsync();
        var held = SeedCsos(connectedSystem, type, 2_400);
        var pages = held.Take(1_500).Chunk(500).Select(chunk => chunk.Select(h => ImportObject(type, h.ExternalId, "Bulk", ObjectChangeType.Updated)).ToList()).ToList();

        var activity = await RunImportAsync(connectedSystem, pages, ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(2_400), "Nothing created again");
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(1_500));
            Assert.That(_spy.PageLookups, Has.Count.EqualTo(2), "The first two pages (1,000 External IDs) are looked up page by page");
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1), "The third would pass half of the 2,400 objects JIM holds");
        }
    }

    [Test]
    public async Task DeltaImport_ObjectHeldUnderAnotherObjectType_IsRefusedWithoutLoadingEveryExternalIdAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        connectedSystem.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = true;
        var userType = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        var computerType = await CreateCsoTypeAsync(connectedSystem.Id, "computer");
        SeedCsos(connectedSystem, userType, 3);
        var heldAsUser = SeedCsos(connectedSystem, userType, 1).Single();

        var activity = await RunImportAsync(connectedSystem, [[ImportObject(computerType, heldAsUser.ExternalId, "Now a computer", ObjectChangeType.Added)]],
            ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity).Select(i => i.ErrorType), Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.ObjectTypeChanged }));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(4), "Nothing created");
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
            // #2835: JIM holds no computer, so its own Object Type is not asked about, only the user type that holds objects.
            Assert.That(_spy.PageLookups.Select(p => p.Select(probe => probe.AttributeId)),
                Is.EqualTo(new[] { new[] { userType.Attributes.Single(a => a.IsExternalId).Id } }),
                "Looked up under the other Object Type holding objects, once");
        }
    }

    [Test]
    public async Task DeltaImport_DeleteWithoutAnObjectType_RetiresTheObjectHeldUnderItsExternalIdWithoutLoadingEveryExternalIdAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        connectedSystem.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = true;
        var userType = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        await CreateCsoTypeAsync(connectedSystem.Id, "computer");
        var held = SeedCsos(connectedSystem, userType, 3);

        var untyped = new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.Deleted,
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, GuidValues = [held[0].ExternalId] }]
        };
        var activity = await RunImportAsync(connectedSystem, [[untyped]], ConnectedSystemRunType.DeltaImport);

        var stored = await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, held[0].Cso.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(stored!.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    /// <summary>
    /// #2835: an OpenLDAP schema declares about a hundred Object Types, and the Connector recommends entryUUID as the
    /// External ID of every one, while the objects JIM holds use two. A deletion arrives without its Object Type and a
    /// group change with it. The page asks the database about each External ID once, under the Object Types it could be
    /// held as: those holding objects, not every type the schema declares, and not a second time for the cross-type check.
    /// </summary>
    [Test]
    public async Task DeltaImport_OpenLdapShapedPage_LooksUpEachExternalIdOnceUnderTheObjectTypesHoldingObjectsAsync()
    {
        var (connectedSystem, userType, groupType) = await ArrangeOpenLdapShapedAsync(schemaOnlyObjectTypes: 20);
        var users = SeedCsos(connectedSystem, userType, 20);
        var groups = SeedCsos(connectedSystem, groupType, 10);
        var deletedUsers = users.Take(10).ToList();
        var newGroupExternalId = Guid.NewGuid();

        var page = deletedUsers.Select(u => UntypedDeletion(userType, u.ExternalId))
            .Concat(groups.Select(g => DirectoryObject(groupType, g.ExternalId, "Renamed", ObjectChangeType.Updated)))
            .Append(DirectoryObject(groupType, newGroupExternalId, "New group", ObjectChangeType.Added))
            .ToList();
        var activity = await RunImportAsync(connectedSystem, [page], ConnectedSystemRunType.DeltaImport);

        var probes = _spy.PageLookups.SelectMany(p => p).ToList();
        var probesPerExternalId = probes.GroupBy(p => p.ValueKey).ToDictionary(g => g.Key, g => g.Count());
        var obsolete = new List<Guid>();
        foreach (var user in users)
        {
            if ((await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, user.Cso.Id))!.Status == ConnectedSystemObjectStatus.Obsolete)
                obsolete.Add(user.Cso.Id);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(obsolete, Is.EquivalentTo(deletedUsers.Select(u => u.Cso.Id)), "Each untyped deletion retires the user it names");
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(10));
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Added), Is.EqualTo(1));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);

            Assert.That(probes.Distinct().Count(), Is.EqualTo(probes.Count), "No External ID is sent to the database twice");
            foreach (var user in deletedUsers)
                Assert.That(probesPerExternalId[user.ExternalId.ToString()], Is.EqualTo(2),
                    "An untyped deletion is looked up under the two Object Types holding objects, not the 22 the schema declares");
            foreach (var group in groups)
                Assert.That(probesPerExternalId[group.ExternalId.ToString()], Is.EqualTo(1), "A group JIM holds is looked up under its own Object Type only");
            Assert.That(probesPerExternalId[newGroupExternalId.ToString()], Is.EqualTo(2),
                "A new group is looked up under its own Object Type, then under the other Object Type holding objects");
            Assert.That(probes, Has.Count.EqualTo(10 * 2 + 10 + 2));
        }
    }

    /// <summary>
    /// #2835: the 500,000-object leaver's Delta Import (untyped deletions, then group changes) looked up 266,580 External
    /// IDs page by page, 5.9 s, before its rule (more than half the objects held) gave way to one load of every object,
    /// 3.0 s. Page lookups now stop before they cost more than that one load would: at about 20 microseconds per External ID
    /// against 6 per object held, 30% of the objects held.
    /// </summary>
    [Test]
    public async Task DeltaImport_LeaverShapedDelta_LoadsEveryExternalIdBeforePageLookupsCostMoreThanOneLoadAsync()
    {
        var (connectedSystem, userType, groupType) = await ArrangeOpenLdapShapedAsync(schemaOnlyObjectTypes: 0);
        var users = SeedCsos(connectedSystem, userType, 3_000);
        var groups = SeedCsos(connectedSystem, groupType, 1_000);
        const int heldCount = 4_000;
        const int externalIdsOneLoadPaysFor = heldCount * 6 / 20;

        var pages = users.Take(1_000).Select(u => UntypedDeletion(userType, u.ExternalId))
            .Concat(groups.Select(g => DirectoryObject(groupType, g.ExternalId, "Renamed", ObjectChangeType.Updated)))
            .Chunk(100).Select(chunk => chunk.ToList()).ToList();
        var activity = await RunImportAsync(connectedSystem, pages, ConnectedSystemRunType.DeltaImport);

        var obsoleteCount = 0;
        foreach (var user in users.Take(1_000))
        {
            if ((await SyncRepo.GetConnectedSystemObjectAsync(connectedSystem.Id, user.Cso.Id))!.Status == ConnectedSystemObjectStatus.Obsolete)
                obsoleteCount++;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(obsoleteCount, Is.EqualTo(1_000));
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(1_000));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(heldCount), "Nothing created");
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
            Assert.That(_spy.PageLookups.Sum(p => p.Count), Is.LessThanOrEqualTo(externalIdsOneLoadPaysFor),
                "Page lookups cost no more than the one load of every object they gave way to");
        }
    }

    /// <summary>
    /// #2835: a Connector that states how many objects it is about to hand over lets the run decide on its first page:
    /// projected over the objects stated, page lookups would cost more than one load of every object, so it loads them
    /// before looking up anything page by page.
    /// </summary>
    [Test]
    public async Task DeltaImport_ConnectorStatesALargeDelta_LoadsEveryExternalIdOnTheFirstPageAsync()
    {
        var (connectedSystem, userType, groupType) = await ArrangeOpenLdapShapedAsync(schemaOnlyObjectTypes: 0);
        var users = SeedCsos(connectedSystem, userType, 3_000);
        var groups = SeedCsos(connectedSystem, groupType, 1_000);

        var pages = users.Take(1_000).Select(u => UntypedDeletion(userType, u.ExternalId))
            .Concat(groups.Select(g => DirectoryObject(groupType, g.ExternalId, "Renamed", ObjectChangeType.Updated)))
            .Chunk(100).Select(chunk => chunk.ToList()).ToList();
        var activity = await RunImportAsync(connectedSystem, pages, ConnectedSystemRunType.DeltaImport, expectedObjectCount: 2_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(1_000));
            Assert.That(_spy.WholeLookupLoads, Is.EqualTo(1));
            Assert.That(_spy.PageLookups, Is.Empty, "Decided from the first page, before any page lookup");
        }
    }

    /// <summary>
    /// #2835: a small delta against a large Connected System stays page by page: its lookups cost a few milliseconds
    /// against a load of every object, and each External ID is asked about once.
    /// </summary>
    [Test]
    public async Task DeltaImport_SmallDeltaAgainstALargeConnectedSystem_LooksUpPageByPageAsync()
    {
        var (connectedSystem, userType, groupType) = await ArrangeOpenLdapShapedAsync(schemaOnlyObjectTypes: 0);
        var users = SeedCsos(connectedSystem, userType, 3_000);
        var groups = SeedCsos(connectedSystem, groupType, 1_000);

        var page = users.Take(50).Select(u => UntypedDeletion(userType, u.ExternalId))
            .Concat(groups.Take(50).Select(g => DirectoryObject(groupType, g.ExternalId, "Renamed", ObjectChangeType.Updated)))
            .ToList();
        var activity = await RunImportAsync(connectedSystem, [page], ConnectedSystemRunType.DeltaImport, expectedObjectCount: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ObjectChangeType == ObjectChangeType.Updated), Is.EqualTo(50));
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
            Assert.That(_spy.PageLookups, Has.Count.EqualTo(1), "Every object was found, so there is no cross-type pass to make");
            Assert.That(_spy.PageLookups.Single(), Has.Count.EqualTo(50 * 2 + 50));
        }
    }

    /// <summary>
    /// #2835: an untyped deletion is looked up only under the Object Types holding objects, yet can still take an Object
    /// Type that held none, from an earlier page that returned the object as new. Asking about it there must answer "none"
    /// rather than fail on a key that was never looked up, and the object is not created.
    /// </summary>
    [Test]
    public async Task DeltaImport_UntypedDeletionOfAnObjectAnEarlierPageReturnedAsNewUnderAnEmptyObjectType_IsNotCreatedAsync()
    {
        var (connectedSystem, userType, _) = await ArrangeOpenLdapShapedAsync(schemaOnlyObjectTypes: 0);
        var deviceType = await CreateCsoTypeAsync(connectedSystem.Id, "device",
        [
            new() { Name = "entryUUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        SeedCsos(connectedSystem, userType, 3);
        var deviceExternalId = Guid.NewGuid();

        var activity = await RunImportAsync(connectedSystem,
            [[ImportObject(deviceType, deviceExternalId, "Printer", ObjectChangeType.Added)], [UntypedDeletion(deviceType, deviceExternalId)]],
            ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(connectedSystem.Id), Is.EqualTo(3), "Added and deleted in one run: not created");
            Assert.That(_spy.WholeLookupLoads, Is.Zero);
        }
    }

    #region Helpers

    private sealed record HeldObject(ConnectedSystemObject Cso, Guid ExternalId);

    /// <summary>
    /// An OpenLDAP-shaped Connected System: External IDs (entryUUID) unique across Object Types, a user and a group type
    /// holding objects, each with the distinguished name as Secondary External ID, and <paramref name="schemaOnlyObjectTypes"/>
    /// further Object Types the schema declares, not selected, carrying entryUUID as External ID as the Connector
    /// recommends for every Object Type.
    /// </summary>
    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType UserType, ConnectedSystemObjectType GroupType)> ArrangeOpenLdapShapedAsync(
        int schemaOnlyObjectTypes)
    {
        var connectedSystem = await CreateConnectedSystemAsync("OpenLDAP");
        connectedSystem.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = true;

        List<ConnectedSystemObjectTypeAttribute> DirectoryAttributes() =>
        [
            new() { Name = "entryUUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ];

        var userType = await CreateCsoTypeAsync(connectedSystem.Id, "inetOrgPerson", DirectoryAttributes());
        var groupType = await CreateCsoTypeAsync(connectedSystem.Id, "groupOfNames", DirectoryAttributes());
        for (var i = 0; i < schemaOnlyObjectTypes; i++)
        {
            var schemaOnly = await CreateCsoTypeAsync(connectedSystem.Id, $"objectClass{i:00}", DirectoryAttributes());
            schemaOnly.Selected = false;
        }

        return (connectedSystem, userType, groupType);
    }

    /// <summary>A deletion as OpenLDAP's access log reports it: the entryUUID, without an Object Type.</summary>
    private static ConnectedSystemImportObject UntypedDeletion(ConnectedSystemObjectType type, Guid externalId) => new()
    {
        ChangeType = ObjectChangeType.Deleted,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute
            {
                Name = type.Attributes.Single(a => a.IsExternalId).Name, Type = AttributeDataType.Guid, GuidValues = [externalId]
            }
        ]
    };

    private static ConnectedSystemImportObject DirectoryObject(ConnectedSystemObjectType type, Guid externalId, string displayName, ObjectChangeType changeType)
    {
        var importObject = ImportObject(type, externalId, displayName, changeType);
        importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
        {
            Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = [$"cn={externalId},ou=Groups,dc=yellowstone,dc=local"]
        });
        return importObject;
    }

    private async Task<(ConnectedSystem ConnectedSystem, ConnectedSystemObjectType Type)> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var type = await CreateCsoTypeAsync(connectedSystem.Id, "user");
        return (connectedSystem, type);
    }

    private async Task<Activity> RunImportAsync(ConnectedSystem connectedSystem, List<List<ConnectedSystemImportObject>> pages,
        ConnectedSystemRunType runType = ConnectedSystemRunType.FullImport, IReadOnlyList<bool>? completeReadByPage = null, int? expectedObjectCount = null)
    {
        var runProfile = await CreateRunProfileAsync(connectedSystem.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(connectedSystem.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages, completeReadByPage: completeReadByPage, expectedObjectCount: expectedObjectCount), connectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private static List<ActivityRunProfileExecutionItem> Errors(Activity activity) =>
        activity.RunProfileExecutionItems
            .Where(item => item.ErrorType != null && item.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

    private List<HeldObject> SeedCsos(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, int count)
    {
        var externalIdAttribute = type.Attributes.Single(a => a.IsExternalId);
        var displayNameAttribute = type.Attributes.Single(a => a.Name == "DisplayName");
        var held = new List<HeldObject>();
        for (var i = 0; i < count; i++)
        {
            var externalId = Guid.NewGuid();
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystem.Id,
                ConnectedSystem = connectedSystem,
                TypeId = type.Id,
                Type = type,
                ExternalIdAttributeId = externalIdAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal,
                Created = DateTime.UtcNow
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = externalIdAttribute.Id, Attribute = externalIdAttribute, GuidValue = externalId, ConnectedSystemObject = cso
            });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = displayNameAttribute.Id, Attribute = displayNameAttribute, StringValue = "Original", ConnectedSystemObject = cso
            });
            SyncRepo.SeedConnectedSystemObject(cso);
            held.Add(new HeldObject(cso, externalId));
        }

        return held;
    }

    private ConnectedSystemObject SeedTextCso(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, string externalId)
    {
        var externalIdAttribute = type.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            TypeId = type.Id,
            Type = type,
            ExternalIdAttributeId = externalIdAttribute.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = externalIdAttribute.Id, Attribute = externalIdAttribute, StringValue = externalId, ConnectedSystemObject = cso
        });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static string?[] DisplayNames(ConnectedSystemObject cso) =>
        cso.AttributeValues.Where(v => v.Attribute?.Name == "DisplayName").Select(v => v.StringValue).ToArray();

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId, string? displayName = null,
        ObjectChangeType changeType = ObjectChangeType.NotSet)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            ChangeType = changeType,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [externalId]
                }
            ]
        };
        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] });
        return importObject;
    }

    private static ConnectedSystemImportObject TextImportObject(ConnectedSystemObjectType type, string externalId, string displayName) => new()
    {
        ObjectType = type.Name,
        ChangeType = ObjectChangeType.Updated,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = type.Attributes.Single(a => a.IsExternalId).Name, Type = AttributeDataType.Text, StringValues = [externalId] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] }
        ]
    };

    #endregion
}
