// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.TestSupport;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Metaverse-Derived Attribute Flows (#1750, plan Phase 4, FR 9): every writer that changes Metaverse attribute values
/// outside a Connected System's own synchronisation marks the joined Connected System Objects of every system whose
/// rules host a derived flow reading a changed attribute (transitively), so that system's next delta or full
/// synchronisation re-derives.
/// </summary>
/// <remarks>
/// Topology: a Person Metaverse Object Type with Employee Id, Account Name, Email, User Principal Name, Nickname and
/// Course.
/// <list type="bullet">
/// <item>HR projects and flows Employee Id; its rule hosts Email = <c>mv["Account Name"] + "@corp.local"</c> (and, for
/// Scenario 5, User Principal Name = <c>mv["Email"]</c>). Optionally contributes Account Name (priority 2).</item>
/// <item>AD joins on Employee Id; optionally contributes Account Name (priority 1); its rule hosts Nickname =
/// <c>mv["Account Name"]</c>.</item>
/// <item>Directory joins; its rule hosts User Principal Name = <c>mv["Email"]</c>, so it reads Account Name only
/// transitively.</item>
/// <item>Training joins and flows Course; it hosts no derived flow and must never be marked.</item>
/// </list>
/// </remarks>
[TestFixture]
public class DerivedInputOutOfSyncMarkingWorkflowTests : WorkflowTestBase
{
    private const string EmailFromAccountName = "mv[\"Account Name\"] + \"@corp.local\"";
    private const string UpnFromEmail = "mv[\"Email\"]";
    private const string NicknameFromAccountName = "mv[\"Account Name\"]";

    /// <summary>
    /// The harness keeps synchronised Metaverse Objects in the in-memory sync repository, by reference, whereas a
    /// direct edit saves through the Metaverse repository (EF), whose store has never seen them. The edit's own save is
    /// therefore routed to a no-op (the in-memory store already holds the edited instance); everything else, marking
    /// included, runs as in production.
    /// </summary>
    [SetUp]
    public void RouteDirectEditSavesToTheInMemoryStore()
    {
        Jim = new JIM.Application.JimApplication(
            MetaverseSaveBypassingRepositoryProxy.Create(Repository), syncRepository: SyncRepo);
    }

    // ---- MetaverseServer.UpdateMetaverseObjectAsync: direct edits ----

    [Test]
    public async Task UpdateMetaverseObjectAsync_AccountNameEdited_MarksEveryHostingSystemTransitivelyAndNoOtherAsync()
    {
        var ctx = await SetUpAsync();
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 1);
        ResetMarks();

        var mvo = people.Single().Mvo;
        await EditAccountNameAsync(ctx, mvo, "jbloggs");

        var person = people.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(person.Hr.DerivedInputChangePending, Is.True, "HR hosts Email, which reads Account Name");
            Assert.That(person.Ad.DerivedInputChangePending, Is.True, "AD hosts Nickname, which reads Account Name; outside synchronisation no system is excluded");
            Assert.That(person.Directory.DerivedInputChangePending, Is.True, "Directory hosts User Principal Name, which reads Email, which reads Account Name");
            Assert.That(person.Training.DerivedInputChangePending, Is.False, "Training hosts no derived flow reading Account Name");
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(1), "one bulk mark for the edit");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_AttributeNoDerivedFlowReadsEdited_MarksNothingAsync()
    {
        var ctx = await SetUpAsync();
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 1);
        ResetMarks();

        var mvo = people.Single().Mvo;
        var value = new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = ctx.Course, AttributeId = ctx.Course.Id, StringValue = "Fire Safety" };
        mvo.AttributeValues.Add(value);
        await Jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [value], removals: [], initiatedByType: ActivityInitiatorType.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Any(c => c.DerivedInputChangePending), Is.False);
            Assert.That(SyncRepo.DerivedInputMarkCalls, Is.Empty, "nothing to mark means no repository call at all");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_OperationalMetadataOnly_MarksNothingAsync()
    {
        // A caller updating only operational metadata passes no additions or removals: nothing changed that a derived
        // flow could read.
        var ctx = await SetUpAsync();
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 1);
        ResetMarks();

        await Jim.Metaverse.UpdateMetaverseObjectAsync(people.Single().Mvo);

        Assert.That(SyncRepo.DerivedInputMarkCalls, Is.Empty);
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_Scenario5DirectAccountNameCorrection_NextHostingDeltaReDerivesEmailAndUpnAsync()
    {
        // PRD Scenario 5 without remediation: Account Name is corrected directly on the Metaverse Object (joe.bloggs
        // to joe.bloggs1). HR hosts both Email and User Principal Name and its own record is unchanged, so only the
        // mark can make its next delta revisit the object.
        var ctx = await SetUpAsync(upnOnHr: true);
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 1);
        var person = people.Single();
        await EditAccountNameAsync(ctx, person.Mvo, "joe.bloggs");
        await RunDeltaSyncAsync(ctx.Hr);
        Assert.That(Text(person.Mvo, ctx.Email), Is.EqualTo("joe.bloggs@corp.local"), "precondition: derived from the first edit");
        Assert.That(person.Hr.DerivedInputChangePending, Is.False, "precondition: the delta cleared the mark");

        await EditAccountNameAsync(ctx, person.Mvo, "joe.bloggs1");
        Assert.That(person.Hr.DerivedInputChangePending, Is.True, "the correction marks the hosting Connected System Object");

        var delta = await RunDeltaSyncAsync(ctx.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.RunProfileExecutionItems.Where(r => r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(Text(person.Mvo, ctx.Email), Is.EqualTo("joe.bloggs1@corp.local"), "HR's delta re-derives Email from the corrected Account Name");
            Assert.That(Text(person.Mvo, ctx.Upn), Is.EqualTo("joe.bloggs1@corp.local"), "and User Principal Name from the re-derived Email, in the same pass");
            Assert.That(person.Hr.DerivedInputChangePending, Is.False, "the mark is cleared once processed");
        }
    }

    // ---- Synchronisation Rule deletion recall ----

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_AccountNameReElected_MarksHostingSystemsInOneBulkUpdateAsync()
    {
        var ctx = await SetUpAsync(adContributesAccountName: true, hrContributesAccountName: true);
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 3);
        ResetMarks();

        var task = await DisableRuleAndBuildRecallTaskAsync(ctx.AdImport);
        await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(people.Select(p => Text(p.Mvo, ctx.AccountName)), Is.EqualTo(people.Select(p => p.HrAccountName)),
                "precondition: HR's Account Name is re-elected");
            Assert.That(people.All(p => p.Hr.DerivedInputChangePending), Is.True, "HR hosts Email, which reads Account Name");
            Assert.That(people.All(p => p.Directory.DerivedInputChangePending), Is.True, "Directory reads Account Name transitively");
            Assert.That(people.Any(p => p.Training.DerivedInputChangePending), Is.False, "Training hosts no derived flow");
            Assert.That(people.Any(p => p.Ad.DerivedInputChangePending), Is.False, "the deleted rule's own derived flow marks nothing");
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(1), "one bulk mark for the batch, never one per object");
            Assert.That(SyncRepo.DerivedInputMarkCalls.Single(), Has.Count.EqualTo(6), "three objects, two hosting systems each");
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_DeletedRuleStillEnabled_ItsOwnDerivedFlowDoesNotMarkItsSystemAsync()
    {
        // The rule is disabled at queue time, so the run-time graph (enabled mappings only) would leave it out anyway.
        // The marking graph must not depend on that: it is built from the rules that survive the deletion, so the
        // deleted rule's derived Nickname, reading the changed Account Name, never marks AD.
        var ctx = await SetUpAsync(adContributesAccountName: true, hrContributesAccountName: true);
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 1);
        ResetMarks();

        var task = await BuildRecallTaskAsync(ctx.AdImport);
        await Jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        var person = people.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(person.Hr.DerivedInputChangePending, Is.True, "precondition: Account Name changed and HR was marked");
            Assert.That(person.Ad.DerivedInputChangePending, Is.False, "the deleted rule's own derived mappings are not hosting flows");
            Assert.That(SyncRepo.DerivedInputMarkCalls.Single().Select(m => m.ConnectedSystemId), Does.Not.Contain(ctx.Ad.Id));
        }
    }

    // ---- Synchronised Deprovisioning ----

    [Test]
    public async Task ExecuteSynchronisedDeprovisioningAsync_AccountNameReElected_MarksHostingSystemsInOneBulkUpdateAsync()
    {
        var ctx = await SetUpAsync(adContributesAccountName: true, hrContributesAccountName: true);
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 3);
        ResetMarks();

        var task = await FenceSystemAndBuildDeprovisioningTaskAsync(ctx.Ad);
        await Jim.ConnectedSystems.ExecuteSynchronisedDeprovisioningAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(people.Select(p => Text(p.Mvo, ctx.AccountName)), Is.EqualTo(people.Select(p => p.HrAccountName)),
                "precondition: HR's Account Name is re-elected");
            Assert.That(people.All(p => p.Hr.DerivedInputChangePending), Is.True, "HR hosts Email, which reads Account Name");
            Assert.That(people.All(p => p.Directory.DerivedInputChangePending), Is.True, "Directory reads Account Name transitively");
            Assert.That(people.Any(p => p.Training.DerivedInputChangePending), Is.False, "Training hosts no derived flow");
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(1), "one bulk mark for the batch, never one per object");
            Assert.That(SyncRepo.DerivedInputMarkCalls.Single().Select(m => m.ConnectedSystemId).Distinct(),
                Is.EquivalentTo(new[] { ctx.Hr.Id, ctx.Directory.Id }), "the deprovisioned system's rules are going away, so they host nothing");
            Assert.That(SyncRepo.DerivedInputMarkCalls.Single(), Has.Count.EqualTo(6));
        }
    }

    // ---- The stranded value sweep ----

    [Test]
    public async Task ExecuteStrandedValueSweepAsync_StrandedAccountNameRecalled_MarksHostingSystemsInOneBulkUpdateAsync()
    {
        var ctx = await SetUpAsync(adContributesAccountName: true, hrContributesAccountName: true);
        var people = await SeedPeopleAndSynchroniseAsync(ctx, 3);

        // A Connector Space clear of AD: its objects are hard-deleted with no obsoletion, so the Account Name it
        // contributed is stranded on the Metaverse Objects with live provenance.
        foreach (var person in people)
        {
            person.Mvo.ConnectedSystemObjects.Remove(person.Ad);
            SyncRepo.RemoveConnectedSystemObject(person.Ad);
        }
        ResetMarks();

        var ad = await ArmSweepAsync(ctx.Ad);
        var activity = await BuildActivityAsync(ad.Id);
        var result = await Jim.ConnectedSystems.ExecuteStrandedValueSweepAsync(ad, activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectsProcessed, Is.EqualTo(3), "precondition: the stranded values are recalled");
            Assert.That(people.Select(p => Text(p.Mvo, ctx.AccountName)), Is.EqualTo(people.Select(p => p.HrAccountName)));
            Assert.That(people.All(p => p.Hr.DerivedInputChangePending), Is.True, "HR hosts Email, which reads Account Name");
            Assert.That(people.All(p => p.Directory.DerivedInputChangePending), Is.True, "Directory reads Account Name transitively");
            Assert.That(people.Any(p => p.Training.DerivedInputChangePending), Is.False, "Training hosts no derived flow");
            Assert.That(SyncRepo.DerivedInputMarkCalls, Has.Count.EqualTo(1), "one bulk mark for the batch, never one per object");
        }
    }

    // ---- Helpers ----

    private sealed record Context(
        MetaverseObjectType PersonType,
        MetaverseAttribute AccountName,
        MetaverseAttribute Email,
        MetaverseAttribute Upn,
        MetaverseAttribute Nickname,
        MetaverseAttribute Course,
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        ConnectedSystem Ad,
        ConnectedSystemObjectType AdType,
        SyncRule AdImport,
        ConnectedSystem Directory,
        ConnectedSystemObjectType DirectoryType,
        ConnectedSystem Training,
        ConnectedSystemObjectType TrainingType);

    private sealed record Person(
        MetaverseObject Mvo,
        ConnectedSystemObject Hr,
        ConnectedSystemObject Ad,
        ConnectedSystemObject Directory,
        ConnectedSystemObject Training,
        string HrAccountName);

    private async Task<Context> SetUpAsync(
        bool upnOnHr = false,
        bool adContributesAccountName = false,
        bool hrContributesAccountName = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var employeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var accountName = await AddMvAttributeAsync(mvType, "Account Name");
        var email = await AddMvAttributeAsync(mvType, "Email");
        var upn = await AddMvAttributeAsync(mvType, "User Principal Name");
        var nickname = await AddMvAttributeAsync(mvType, "Nickname");
        var course = await AddMvAttributeAsync(mvType, "Course");

        var (hr, hrType, hrImport) = await CreateSourceAsync("HR", mvType, employeeId, project: true);
        if (hrContributesAccountName)
            FromAttribute(hrImport, accountName, hrType.Attributes.Single(a => a.Name == "accountName")).Priority = 2;
        FromExpression(hrImport, email, EmailFromAccountName);
        if (upnOnHr)
            FromExpression(hrImport, upn, UpnFromEmail);

        var (ad, adType, adImport) = await CreateSourceAsync("AD", mvType, employeeId, project: false);
        if (adContributesAccountName)
            FromAttribute(adImport, accountName, adType.Attributes.Single(a => a.Name == "accountName")).Priority = 1;
        FromExpression(adImport, nickname, NicknameFromAccountName);

        var (directory, directoryType, directoryImport) = await CreateSourceAsync("Directory", mvType, employeeId, project: false);
        if (!upnOnHr)
            FromExpression(directoryImport, upn, UpnFromEmail);

        var (training, trainingType, trainingImport) = await CreateSourceAsync("Training", mvType, employeeId, project: false);
        FromAttribute(trainingImport, course, trainingType.Attributes.Single(a => a.Name == "course"));

        await DbContext.SaveChangesAsync();

        return new Context(mvType, accountName, email, upn, nickname, course, hr, hrType, ad, adType, adImport,
            directory, directoryType, training, trainingType);
    }

    private async Task<(ConnectedSystem System, ConnectedSystemObjectType Type, SyncRule Import)> CreateSourceAsync(
        string name, MetaverseObjectType mvType, MetaverseAttribute employeeId, bool project)
    {
        var system = await CreateConnectedSystemAsync(name);
        var type = await CreateCsoTypeAsync(system.Id, $"{name}Person", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "employeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "accountName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "course", Type = AttributeDataType.Text, Selected = true }
        });
        var csEmployeeId = type.Attributes.Single(a => a.Name == "employeeId");

        var import = await CreateImportSyncRuleAsync(system.Id, type, mvType, $"{name} Import", enableProjection: project);
        FromAttribute(import, employeeId, csEmployeeId);
        if (!project)
        {
            import.ObjectMatchingRules.Add(new ObjectMatchingRule
            {
                SyncRule = import,
                SyncRuleId = import.Id,
                Order = 0,
                CaseSensitive = true,
                TargetMetaverseAttribute = employeeId,
                TargetMetaverseAttributeId = employeeId.Id,
                Sources = new List<ObjectMatchingRuleSource>
                {
                    new() { Order = 0, ConnectedSystemAttribute = csEmployeeId, ConnectedSystemAttributeId = csEmployeeId.Id }
                }
            });
        }

        return (system, type, import);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> people in every system and runs each system's Full Synchronisation (HR first,
    /// which projects; the others join), so every Metaverse Object is joined in all four.
    /// </summary>
    private async Task<List<Person>> SeedPeopleAndSynchroniseAsync(Context ctx, int count)
    {
        var seeded = Enumerable.Range(1, count).Select(i =>
        {
            var employeeId = $"E{i}";
            var hrAccountName = $"hr.person{i}";
            return (
                Hr: SeedCso(ctx.Hr, ctx.HrType, ("employeeId", employeeId), ("accountName", hrAccountName)),
                Ad: SeedCso(ctx.Ad, ctx.AdType, ("employeeId", employeeId), ("accountName", $"ad.person{i}")),
                Directory: SeedCso(ctx.Directory, ctx.DirectoryType, ("employeeId", employeeId)),
                Training: SeedCso(ctx.Training, ctx.TrainingType, ("employeeId", employeeId), ("course", "Induction")),
                HrAccountName: hrAccountName);
        }).ToList();

        await RunFullSyncAsync(ctx.Hr);
        await RunFullSyncAsync(ctx.Ad);
        await RunFullSyncAsync(ctx.Directory);
        await RunFullSyncAsync(ctx.Training);

        return seeded.Select(s => new Person(
            SyncRepo.MetaverseObjects[s.Hr.MetaverseObjectId ?? throw new InvalidOperationException("HR did not project")],
            s.Hr, s.Ad, s.Directory, s.Training, s.HrAccountName)).ToList();
    }

    /// <summary>
    /// Clears every mark the set-up synchronisations wrote, so a test observes only the writer under test.
    /// </summary>
    private void ResetMarks()
    {
        foreach (var cso in SyncRepo.ConnectedSystemObjects.Values)
            cso.DerivedInputChangePending = false;
        SyncRepo.DerivedInputMarkCalls.Clear();
    }

    /// <summary>
    /// A direct edit through <c>MetaverseServer.UpdateMetaverseObjectAsync</c>, as the portal or API would make one:
    /// the value is applied to the object and handed over as the change set.
    /// </summary>
    private async Task EditAccountNameAsync(Context ctx, MetaverseObject mvo, string accountName)
    {
        var removals = mvo.AttributeValues.Where(av => av.AttributeId == ctx.AccountName.Id || av.Attribute?.Id == ctx.AccountName.Id).ToList();
        foreach (var removal in removals)
            mvo.AttributeValues.Remove(removal);

        var addition = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = ctx.AccountName,
            AttributeId = ctx.AccountName.Id,
            StringValue = accountName
        };
        mvo.AttributeValues.Add(addition);

        await Jim.Metaverse.UpdateMetaverseObjectAsync(mvo, additions: [addition], removals: removals,
            initiatedByType: ActivityInitiatorType.User, initiatedByName: "Test Administrator");
    }

    private async Task<MetaverseAttribute> AddMvAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();
        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);
        return attribute;
    }

    private static SyncRuleMapping FromAttribute(SyncRule rule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
        rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    private static void FromExpression(SyncRule rule, MetaverseAttribute target, string expression) =>
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = expression, MissingInputBehaviour = MissingInputBehaviour.ContributeNoValue } }
        });

    private ConnectedSystemObject SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, params (string Name, string? Value)[] values)
    {
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            TypeId = type.Id,
            Type = type,
            Created = DateTime.UtcNow.AddMinutes(-10)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = Guid.NewGuid() });
        foreach (var (name, value) in values.Where(v => v.Value != null))
        {
            var attribute = type.Attributes.Single(a => a.Name == name);
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, StringValue = value });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static string? Text(MetaverseObject mvo, MetaverseAttribute attribute) =>
        mvo.AttributeValues
            .Where(av => (av.AttributeId == attribute.Id || av.Attribute?.Id == attribute.Id) && !av.NullValue)
            .Select(av => av.StringValue)
            .SingleOrDefault();

    /// <summary>
    /// Detaches processor-modified entities (the full synchronisations leave tracked entities in states the in-memory
    /// store no longer recognises), the guard the other server-path workflow tests apply before saving.
    /// </summary>
    private void DetachModifiedEntities()
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Modified).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    /// <summary>
    /// Disables <paramref name="rule"/> and builds its deletion recall task, as <c>DeleteSyncRuleAsync</c> does at queue
    /// time.
    /// </summary>
    private async Task<DeleteSyncRuleWorkerTask> DisableRuleAndBuildRecallTaskAsync(SyncRule rule)
    {
        DetachModifiedEntities();
        rule.Enabled = false;
        rule.DisabledReason = "Deletion in progress: contributed attribute values are being recalled.";
        DbContext.Entry(rule).State = Microsoft.EntityFrameworkCore.EntityState.Modified;
        await DbContext.SaveChangesAsync();
        return await BuildRecallTaskAsync(rule);
    }

    private async Task<DeleteSyncRuleWorkerTask> BuildRecallTaskAsync(SyncRule rule)
    {
        DetachModifiedEntities();
        var activity = new Activity
        {
            TargetName = rule.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.RecallAttributeValues,
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = rule.ConnectedSystemId,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        return new DeleteSyncRuleWorkerTask(rule.Id, recallContributedValues: true) { Activity = activity };
    }

    private async Task<DeleteConnectedSystemWorkerTask> FenceSystemAndBuildDeprovisioningTaskAsync(ConnectedSystem system)
    {
        DetachModifiedEntities();
        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.Status = ConnectedSystemStatus.Deleting;
        await DbContext.SaveChangesAsync();

        var activity = new Activity
        {
            TargetName = system.Name,
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Deprovision,
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow
        };
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        return new DeleteConnectedSystemWorkerTask(system.Id, evaluateMvoDeletionRules: true, deleteChangeHistory: false)
        {
            SynchronisedDeprovisioning = true,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Test Administrator",
            Activity = activity
        };
    }

    /// <summary>
    /// Arms the stranded value sweep with the #1605 gate open: a clear ten minutes ago, then a successful Full Import.
    /// </summary>
    private async Task<ConnectedSystem> ArmSweepAsync(ConnectedSystem system)
    {
        DetachModifiedEntities();
        var persistedSystem = await DbContext.ConnectedSystems.FindAsync(system.Id);
        persistedSystem!.StrandedValueSweepArmedAt = DateTime.UtcNow.AddMinutes(-10);
        persistedSystem.LastSuccessfulFullImportCompletedAt = DateTime.UtcNow.AddMinutes(-5);
        await DbContext.SaveChangesAsync();
        return persistedSystem;
    }

    private async Task<Activity> BuildActivityAsync(int connectedSystemId)
    {
        var profile = await CreateRunProfileAsync(connectedSystemId, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        return await CreateActivityAsync(connectedSystemId, profile, ConnectedSystemRunType.FullSynchronisation);
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        var processor = new SyncFullSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim),
            SyncRepo, reloaded, profile, activity, new CancellationTokenSource());
        await processor.PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        var processor = new SyncDeltaSyncTaskProcessor(new JIM.Application.Servers.SyncEngine(), new JIM.Application.Servers.SyncServer(Jim),
            SyncRepo, reloaded, profile, activity, new CancellationTokenSource());
        await processor.PerformDeltaSyncAsync();
        return activity;
    }
}
