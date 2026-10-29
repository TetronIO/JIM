// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A saved configuration change awaiting synchronisation (#2615) makes the Connected System's next synchronisation,
/// Delta or Full, process every object of the affected Connected System Object Type, and be marked applied once the run
/// completes. A Delta Synchronisation otherwise visits only the objects whose own data changed, so an import rule change
/// would reach existing objects only when a Full Synchronisation happened to run.
/// <para>
/// The fixture's Connected System holds two types (User and Contact, three objects each), each projecting to its own
/// Metaverse Object Type, so "every object of the type, and only the modified objects of the others" is observable.
/// </para>
/// </summary>
[TestFixture]
public class ConfigurationChangeSynchronisationWorkflowTests : WorkflowTestBase
{
    private const int ObjectsPerType = 3;
    private const string ChangeReason = "Import rule scope changed";

    private ConnectedSystem _system = null!;
    private ConnectedSystemObjectType _userType = null!;
    private ConnectedSystemObjectType _contactType = null!;
    private SyncRule _userRule = null!;
    private MetaverseAttribute _mvPersonEmployeeId = null!;
    private ConnectedSystemObjectTypeAttribute _userEmployeeIdAttribute = null!;
    private ConnectedSystemObjectType _emptyType = null!;
    private ConnectedSystem _otherSystem = null!;
    private ConnectedSystemObjectType _otherSystemType = null!;
    private ConnectedSystem _directory = null!;
    private ConnectedSystemObjectTypeAttribute _directoryDisplayNameAttribute = null!;
    private List<ConnectedSystemObject> _users = null!;
    private List<ConnectedSystemObject> _contacts = null!;

    /// <summary>
    /// The Connected System with User and Contact types, an import rule for each (User flows DisplayName only, so a test
    /// can later "save" a change by adding the EmployeeId mapping), three objects of each type, and a first Full
    /// Synchronisation already run so that the watermark is set and nothing is modified since.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        _system = await CreateConnectedSystemAsync("HR");
        _userType = await CreateCsoTypeAsync(_system.Id, "User");
        _contactType = await CreateCsoTypeAsync(_system.Id, "Contact");
        var mvPerson = await CreateMvObjectTypeAsync("Person");
        var mvContact = await CreateMvObjectTypeAsync("Contact");

        _userRule = await CreateImportSyncRuleAsync(_system.Id, _userType, mvPerson, "User Import");
        _userRule.AttributeFlowRules.Add(Mapping(_userRule, mvPerson, "DisplayName", _userType, "DisplayName"));
        var contactRule = await CreateImportSyncRuleAsync(_system.Id, _contactType, mvContact, "Contact Import");
        contactRule.AttributeFlowRules.Add(Mapping(contactRule, mvContact, "DisplayName", _contactType, "DisplayName"));
        await DbContext.SaveChangesAsync();

        _mvPersonEmployeeId = mvPerson.Attributes.First(a => a.Name == "EmployeeId");
        _userEmployeeIdAttribute = _userType.Attributes.Single(a => a.Name == "EmployeeId");

        // A directory the Person's DisplayName is exported to, so a test can observe export evaluation following.
        _directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(_directory.Id, "Account");
        var exportRule = await CreateExportSyncRuleAsync(_directory.Id, directoryType, mvPerson, "Directory Export");
        _directoryDisplayNameAttribute = directoryType.Attributes.Single(a => a.Name == "DisplayName");
        var mvDisplayName = mvPerson.Attributes.First(a => a.Name == "DisplayName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, SyncRuleId = exportRule.Id,
            TargetConnectedSystemAttribute = _directoryDisplayNameAttribute, TargetConnectedSystemAttributeId = _directoryDisplayNameAttribute.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayName, MetaverseAttributeId = mvDisplayName.Id } }
        });
        await DbContext.SaveChangesAsync();

        // A type holding no objects, and a second Connected System. Created here, before any run: the harness cannot
        // save new configuration once a processor has modified what its context tracks.
        _emptyType = await CreateCsoTypeAsync(_system.Id, "Group");
        _otherSystem = await CreateConnectedSystemAsync("Other");
        _otherSystemType = await CreateCsoTypeAsync(_otherSystem.Id, "User");

        _users = await CreateCsosAsync(_system.Id, _userType, ObjectsPerType, "User");
        _contacts = await CreateCsosAsync(_system.Id, _contactType, ObjectsPerType, "Contact");

        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
    }

    [Test]
    public async Task DeltaSync_ChangeAwaitingSynchronisationForAType_ProcessesEveryObjectOfThatTypeAndOnlyModifiedObjectsOfOthersThenAppliesItAsync()
    {
        // The change: the User import rule now also flows EmployeeId. No object's own data has changed.
        AddEmployeeIdMapping();

        var control = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(control.ProcessedCsoIds, Is.Empty, "control: with no change awaiting synchronisation a Delta Synchronisation reaches nothing");
            Assert.That(PersonEmployeeIds(), Is.Empty, "control: and so the new mapping has not reached any existing Metaverse Object");
        }

        var awaiting = RecordChange(_userType);
        // One modified User (must be counted and processed once, not twice) and one modified Contact, after the watermark.
        await Task.Delay(50);
        await ModifyCsoAsync(_users[0]);
        await ModifyCsoAsync(_contacts[0]);

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.ProcessedCsoIds.Order(), Is.EqualTo(_users.Select(u => u.Id).Append(_contacts[0].Id).Order()),
                "every User, including the unchanged ones, and only the modified Contact");
            Assert.That(run.ProcessedCsoIds, Is.Unique, "no object is processed twice");
            Assert.That(run.Activity.ObjectsToProcess, Is.EqualTo(ObjectsPerType + 1), "the progress estimate counts the type in full, and the modified Contact");
            Assert.That(PersonEmployeeIds(), Has.Count.EqualTo(ObjectsPerType), "the rule change has reached every existing User's Metaverse Object");
            Assert.That(awaiting.ClaimedByActivityId, Is.EqualTo(run.Activity.Id));
            Assert.That(awaiting.AppliedAt, Is.Not.Null, "the run marked the change applied");
        }

        var followUp = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(followUp.ProcessedCsoIds, Is.Empty, "once applied, the next Delta Synchronisation is back to modified objects only");
    }

    [Test]
    public async Task DeltaSync_ChangeSavedAfterTheRunClaimed_IsNotAppliedByThatRunAndIsClaimedByTheNextAsync()
    {
        var first = RecordChange(_userType);
        ConfigurationChangeSynchronisation? savedMidRun = null;

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, onProcessed: _ =>
        {
            // A second change is saved while the run is processing the User type.
            savedMidRun ??= RecordChange(_userType);
        });

        Assert.That(savedMidRun, Is.Not.Null, "precondition: the second change was saved during the run");
        var second = savedMidRun!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.AppliedAt, Is.Not.Null, "the change the run claimed is applied");
            Assert.That(second.ClaimedByActivityId, Is.Null, "the change saved mid-run was not claimed");
            Assert.That(second.AppliedAt, Is.Null, "and so it stays awaiting synchronisation, though the run processed the type");
        }

        var next = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.ProcessedCsoIds, Has.Count.EqualTo(ObjectsPerType), "the next run processes the type in full again");
            Assert.That(second.ClaimedByActivityId, Is.EqualTo(next.Activity.Id));
            Assert.That(second.AppliedAt, Is.Not.Null);
            Assert.That(first.ClaimedByActivityId, Is.EqualTo(run.Activity.Id), "the first change keeps the run that applied it");
        }
    }

    [Test]
    public async Task DeltaSync_ChangeCommittedAfterTheEligibleIdsWereReadButBeforeTheClaim_IsNotClaimedAndIsClaimedByTheNextRunAsync()
    {
        // The window the worker's order closes: a change saved after the worker read the ids and before the run claims
        // may be missing from the Connected System and Object Types the worker has already loaded, so it must wait.
        var first = RecordChange(_userType);
        ConfigurationChangeSynchronisation? lateChange = null;

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, afterEligibleIdsRead: () => lateChange = RecordChange(_userType));

        Assert.That(lateChange, Is.Not.Null, "precondition: the change was committed in the gap");
        var late = lateChange!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.AppliedAt, Is.Not.Null, "the change read before is claimed and applied");
            Assert.That(late.ClaimedByActivityId, Is.Null, "the change committed in the gap is not claimed");
            Assert.That(late.AppliedAt, Is.Null);
        }

        var next = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.ProcessedCsoIds.Order(), Is.EqualTo(_users.Select(u => u.Id).Order()), "the next run processes the type in full again");
            Assert.That(late.ClaimedByActivityId, Is.EqualTo(next.Activity.Id));
            Assert.That(late.AppliedAt, Is.Not.Null);
            Assert.That(first.ClaimedByActivityId, Is.EqualTo(run.Activity.Id));
        }
    }

    [Test]
    public async Task DeltaSync_ProcessorConstructedWithoutEligibleIds_ClaimsNothingAsync()
    {
        var awaiting = RecordChange(_userType);
        var reloaded = await ReloadEntityAsync(_system);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"Delta {Guid.NewGuid():N}", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);

        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaiting.ClaimedByActivityId, Is.Null);
            Assert.That(awaiting.AppliedAt, Is.Null, "a run handed no ids has nothing it may claim or apply");
        }
    }

    [Test]
    public async Task DeltaSync_TenChangesForOneType_ProcessesEachObjectOnceAndAppliesAllTenAsync()
    {
        var changes = Enumerable.Range(0, 10).Select(i => RecordChange(_userType, $"{ChangeReason} {i}")).ToList();

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.ProcessedCsoIds.Order(), Is.EqualTo(_users.Select(u => u.Id).Order()), "the type is processed once, not once per change");
            Assert.That(changes.Select(c => c.AppliedAt), Has.All.Not.Null, "every one of the ten changes is applied");
            Assert.That(changes.Select(c => c.ClaimedByActivityId), Has.All.EqualTo(run.Activity.Id));
        }
    }

    [Test]
    public async Task DeltaSync_Cancelled_AppliesNothingAndTheNextRunProcessesTheTypeInFullAsync()
    {
        var awaiting = RecordChange(_userType);
        var cts = new CancellationTokenSource();

        var cancelled = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, onProcessed: _ => cts.Cancel(), cts: cts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cancelled.ProcessedCsoIds, Has.Count.LessThan(ObjectsPerType), "precondition: the run stopped part way through the type");
            Assert.That(awaiting.AppliedAt, Is.Null, "a cancelled run applies nothing");
            Assert.That(awaiting.ClaimedByActivityId, Is.EqualTo(cancelled.Activity.Id), "the cancelled run's claim is left in place");
        }

        var next = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.ProcessedCsoIds.Order(), Is.EqualTo(_users.Select(u => u.Id).Order()), "the next run processes the type in full");
            Assert.That(awaiting.ClaimedByActivityId, Is.EqualTo(next.Activity.Id), "and reclaims the change from the cancelled run");
            Assert.That(awaiting.AppliedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task DeltaSync_FailedRun_AppliesNothingAndTheNextRunReclaimsItAsync()
    {
        var awaiting = RecordChange(_userType);

        Assert.That(async () => await RunAsync(ConnectedSystemRunType.DeltaSynchronisation,
                onProcessed: _ => throw new InvalidOperationException("simulated failure part way through the type")),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(awaiting.AppliedAt, Is.Null, "a run that fails applies nothing");

        var next = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(next.ProcessedCsoIds.Order(), Is.EqualTo(_users.Select(u => u.Id).Order()));
            Assert.That(awaiting.ClaimedByActivityId, Is.EqualTo(next.Activity.Id));
            Assert.That(awaiting.AppliedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task DeltaSync_NothingModifiedButAChangeAwaitingSynchronisation_DoesNotCompleteEarlyAsync()
    {
        var awaiting = RecordChange(_userType);

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Activity.Message, Is.Not.EqualTo("No changes to process"), "the early exit must wait for claimed changes");
            Assert.That(run.Activity.ObjectsToProcess, Is.EqualTo(ObjectsPerType));
            Assert.That(run.ProcessedCsoIds, Has.Count.EqualTo(ObjectsPerType));
            Assert.That(awaiting.AppliedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task DeltaSync_ChangeForATypeWithNoObjects_StillAppliesItAsync()
    {
        var awaiting = RecordChange(_emptyType);

        var run = await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.ProcessedCsoIds, Is.Empty);
            Assert.That(awaiting.AppliedAt, Is.Not.Null, "a change cannot stay awaiting synchronisation for ever because its type holds no objects");
        }
    }

    [Test]
    public async Task DeltaSync_ChangesAwaitingSynchronisationForAnotherSystem_AreNeitherClaimedNorAppliedAsync()
    {
        var foreign = SyncRepo.SeedConfigurationChangeSynchronisation(new ConfigurationChangeSynchronisation
        {
            ConnectedSystemId = _otherSystem.Id, ConnectedSystemObjectTypeId = _otherSystemType.Id, Reason = ChangeReason
        });
        RecordChange(_userType);

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foreign.ClaimedByActivityId, Is.Null);
            Assert.That(foreign.AppliedAt, Is.Null);
        }
    }

    [Test]
    public async Task FullSync_ChangesAwaitingSynchronisation_AppliesAllOfThemAsync()
    {
        var changes = new[] { RecordChange(_userType), RecordChange(_contactType), RecordChange(_userType) };

        var run = await RunAsync(ConnectedSystemRunType.FullSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.ProcessedCsoIds, Has.Count.EqualTo(2 * ObjectsPerType), "a Full Synchronisation processes everything anyway");
            Assert.That(changes.Select(c => c.AppliedAt), Has.All.Not.Null);
            Assert.That(changes.Select(c => c.ClaimedByActivityId), Has.All.EqualTo(run.Activity.Id));
        }
    }

    [Test]
    public async Task FullSync_Cancelled_AppliesNothingAsync()
    {
        var awaiting = RecordChange(_userType);
        var cts = new CancellationTokenSource();

        await RunAsync(ConnectedSystemRunType.FullSynchronisation, onProcessed: _ => cts.Cancel(), cts: cts);

        Assert.That(awaiting.AppliedAt, Is.Null, "a cancelled Full Synchronisation applies nothing");
    }

    [Test]
    public async Task DeltaSync_ImportRuleChangeAppliedToUnchangedObjects_StagesTheirExportsAsUsualAsync()
    {
        var exportedBefore = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == _directory.Id).ToList();
        Assert.That(exportedBefore, Has.Count.EqualTo(ObjectsPerType), "precondition: the baseline provisioned each User into the directory");

        // The saved import rule change: DisplayName now comes from the EmployeeId attribute.
        var displayNameMapping = _userRule.AttributeFlowRules.Single();
        displayNameMapping.Sources.Single().ConnectedSystemAttribute = _userEmployeeIdAttribute;
        displayNameMapping.Sources.Single().ConnectedSystemAttributeId = _userEmployeeIdAttribute.Id;
        RecordChange(_userType);

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        var expected = _users.Select(u => u.AttributeValues.Single(av => av.AttributeId == _userEmployeeIdAttribute.Id).StringValue).Order().ToList();
        var staged = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == _directory.Id)
            .Select(pe => pe.AttributeValueChanges.Single(c => c.AttributeId == _directoryDisplayNameAttribute.Id).StringValue)
            .Order()
            .ToList();
        Assert.That(staged, Is.EqualTo(expected),
            "the Metaverse Objects reprocessed only because their type was processed in full are export-evaluated as usual: no second mechanism");
    }

    [Test]
    public async Task DeltaSync_ClaimsBeforeCountingAndBeforeLoadingTheSynchronisationRulesAsync()
    {
        RecordChange(_userType);
        var claimedWhenRulesLoaded = new List<bool>();
        var claimedWhenCounted = new List<bool>();
        bool Claimed() => SyncRepo.ConfigurationChangeSynchronisations.All(c => c.ClaimedByActivityId != null);

        var repository = InterceptingSyncRepositoryProxy.Create(
            InterceptingSyncRepositoryProxy.Create(SyncRepo, nameof(ISyncRepository.GetSyncRulesAsync), () => claimedWhenRulesLoaded.Add(Claimed())),
            nameof(ISyncRepository.GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync), () => claimedWhenCounted.Add(Claimed()));

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, repository: repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimedWhenCounted, Is.Not.Empty, "precondition: the count was taken");
            Assert.That(claimedWhenCounted, Has.All.True, "the claim precedes the count");
            Assert.That(claimedWhenRulesLoaded, Is.Not.Empty, "precondition: the rules were loaded");
            Assert.That(claimedWhenRulesLoaded, Has.All.True,
                "the claim precedes loading any Synchronisation Rule: that order is what makes a claimed change visible in the rules the run applies");
        }
    }

    [Test]
    public async Task FullSync_ClaimsBeforeLoadingTheSynchronisationRulesAsync()
    {
        RecordChange(_userType);
        var claimedWhenRulesLoaded = new List<bool>();

        var repository = InterceptingSyncRepositoryProxy.Create(SyncRepo, nameof(ISyncRepository.GetSyncRulesAsync),
            () => claimedWhenRulesLoaded.Add(SyncRepo.ConfigurationChangeSynchronisations.All(c => c.ClaimedByActivityId != null)));

        await RunAsync(ConnectedSystemRunType.FullSynchronisation, repository: repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimedWhenRulesLoaded, Is.Not.Empty, "precondition: the rules were loaded");
            Assert.That(claimedWhenRulesLoaded, Has.All.True, "the claim precedes loading any Synchronisation Rule");
        }
    }

    private ConfigurationChangeSynchronisation RecordChange(ConnectedSystemObjectType type, string reason = ChangeReason)
        => SyncRepo.SeedConfigurationChangeSynchronisation(new ConfigurationChangeSynchronisation
        {
            ConnectedSystemId = _system.Id, ConnectedSystemObjectTypeId = type.Id, Reason = reason
        });

    private void AddEmployeeIdMapping()
        => _userRule.AttributeFlowRules.Add(Mapping(_userRule, _userRule.MetaverseObjectType!, "EmployeeId", _userType, "EmployeeId"));

    private static SyncRuleMapping Mapping(SyncRule rule, MetaverseObjectType mvType, string mvAttributeName, ConnectedSystemObjectType csoType, string csoAttributeName)
    {
        var mvAttribute = mvType.Attributes.First(a => a.Name == mvAttributeName);
        var csoAttribute = csoType.Attributes.Single(a => a.Name == csoAttributeName);
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = mvAttribute,
            TargetMetaverseAttributeId = mvAttribute.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csoAttribute, ConnectedSystemAttributeId = csoAttribute.Id } }
        };
    }

    /// <summary>
    /// The EmployeeId values the Person Metaverse Objects currently hold.
    /// </summary>
    private List<string?> PersonEmployeeIds() => SyncRepo.MetaverseObjects.Values
        .SelectMany(m => m.AttributeValues)
        .Where(av => av.AttributeId == _mvPersonEmployeeId.Id && !av.NullValue)
        .Select(av => av.StringValue)
        .ToList();

    private sealed record RunResult(Activity Activity, List<Guid> ProcessedCsoIds);

    /// <summary>
    /// Runs a synchronisation of the fixture's Connected System, recording every object processed.
    /// </summary>
    /// <param name="onProcessed">Called after each object is processed, for a test to act at an exact point in the run.</param>
    /// <param name="cts">Lets a test cancel the run.</param>
    /// <param name="repository">Wraps the synchronisation repository, for a test to observe call order.</param>
    /// <param name="afterEligibleIdsRead">Called once the worker's read of the ids awaiting synchronisation is done and
    /// before the processor starts, for a test to commit a change in that gap.</param>
    private async Task<RunResult> RunAsync(
        ConnectedSystemRunType runType,
        Action<ConnectedSystemObject>? onProcessed = null,
        CancellationTokenSource? cts = null,
        ISyncRepository? repository = null,
        Action? afterEligibleIdsRead = null)
    {
        var reloaded = await ReloadEntityAsync(_system);
        // As the worker does, before it loads the Connected System: read the ids awaiting synchronisation. They are the
        // only changes the run may claim.
        var eligibleIds = await SyncRepo.GetConfigurationChangeSynchronisationIdsAwaitingAsync(reloaded.Id);
        afterEligibleIdsRead?.Invoke();
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{runType} {Guid.NewGuid():N}", runType);
        var activity = await CreateActivityAsync(reloaded.Id, profile, runType);
        var processed = new List<Guid>();
        void Record(ConnectedSystemObject cso)
        {
            processed.Add(cso.Id);
            onProcessed?.Invoke(cso);
        }

        cts ??= new CancellationTokenSource();
        var syncRepository = repository ?? SyncRepo;
        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            var processor = new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), syncRepository, reloaded, profile, activity, cts,
                configurationChangeIdsEligibleToClaim: eligibleIds)
            {
                OnConnectedSystemObjectProcessedInPass2 = Record
            };
            await processor.PerformFullSyncAsync();
        }
        else
        {
            var processor = new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), syncRepository, reloaded, profile, activity, cts,
                configurationChangeIdsEligibleToClaim: eligibleIds)
            {
                OnConnectedSystemObjectProcessedInPass2 = Record
            };
            await processor.PerformDeltaSyncAsync();
        }

        return new RunResult(activity, processed);
    }
}
