// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Issue #2615: a Metaverse Object JIM creates or changes outside any synchronisation (a first sign-in, a profile
/// refresh at sign-in, a value set through the portal or API, example data) reaches the target systems at the next
/// synchronisation of ANY Connected System, whichever order things happened in, and values and removals included.
/// Scope was already reviewed for flagged objects; these tests pin the rest: a new person is provisioned whether the
/// rule or the person came first, a changed value is staged for the account the person already has, a removed value is
/// removed from it and nothing else the account holds is touched.
///
/// Topology: an "HR" source projecting Alice and Bob (Employee Id, Display Name), whose synchronisations are the
/// unrelated runs that deliver the changes, and a "Directory" target holding an account for each, with an Email and a
/// multi-valued Aliases attribute that HR does not supply. Every edit goes through
/// <see cref="MetaverseServer.UpdateMetaverseObjectAsync"/> and every creation through
/// <see cref="MetaverseServer.CreateMetaverseObjectAsync"/>, the path the portal, REST API and sign-in share.
/// </summary>
[TestFixture]
public class UnsynchronisedChangeDeliveryWorkflowTests : WorkflowTestBase
{
    private const string AliceAliasKept = "alice@corp.local";
    private const string AliceAliasRemoved = "a.smith@corp.local";
    private const string AliasOnlyTheDirectoryHolds = "helpdesk-added@corp.local";

    /// <summary>
    /// The harness keeps synchronised Metaverse Objects in the in-memory sync repository, by reference, whereas an edit
    /// saves through the Metaverse repository (EF), whose store has never seen them. The edit's own save is routed to a
    /// no-op (the in-memory store already holds the edited instance); everything else, recording included, runs as in
    /// production.
    /// </summary>
    [SetUp]
    public void RouteDirectEditSavesToTheInMemoryStore()
    {
        Jim = new JimApplication(MetaverseSaveBypassingRepositoryProxy.Create(Repository), syncRepository: SyncRepo);
    }

    #region A new person is provisioned, whichever came first

    [Test]
    public async Task DeltaSync_PersonCreatedBeforeTheExportRuleExisted_IsProvisionedAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, withExportRule: false);
        await CreatePersonAsync(ctx, "E9", "Jay Admin");

        await CreateProvisioningExportRuleAsync(ctx);
        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Jay Admin" }), "saving the rule flags every person, the new one included");
    }

    [Test]
    public async Task DeltaSync_PersonCreatedAfterTheExportRuleExisted_IsProvisionedAsync()
    {
        // The case that was never provisioned: nothing flagged the person, and the rule had already been saved.
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true);
        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob" }), "arrange: the rule provisioned the people HR supplied");
        await CreatePersonAsync(ctx, "E9", "Jay Admin");

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Jay Admin" }), "the outcome is the same as when the person came first");
    }

    [Test]
    public async Task DeltaSync_PersonCreatedAndFlagged_HasItsFlagClearedOnceReviewedAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true);
        var jay = await CreatePersonAsync(ctx, "E9", "Jay Admin");
        Assert.That(jay.ScopeReviewPending, Is.True, "arrange: creating the person flagged it");

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(jay.ScopeReviewPending, Is.False);
    }

    #endregion

    #region A changed value reaches the account the person already has

    [Test]
    public async Task DeltaSync_ValueChangedOutsideSynchronisation_StagesAnUpdateForThatAttributeAloneAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", "alice@new.local");

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Email", PendingExportAttributeChangeType.Update, (string?)"alice@new.local") }),
                "the changed attribute only; the account's other attributes are not re-sent");
            Assert.That(PendingExportsFor(ctx.Directory).Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Update }));
            Assert.That(activity.RunProfileExecutionItems.Select(i => i.DisplayNameSnapshot), Is.EqualTo(new[] { ItemNameOf(ctx, "Alice") }),
                "the item for the person whose value was delivered is kept, though the review made no scope change");
            Assert.That(MetaverseObjectOf(ctx, "Alice").ScopeReviewPending, Is.False, "delivered, so the flag is cleared");
            Assert.That(MetaverseObjectOf(ctx, "Alice").UnsynchronisedChangesJson, Is.Null, "and so is the record of what changed");
        }
    }

    [Test]
    public async Task DeltaSync_ValueClearedOutsideSynchronisation_StagesAClearingUpdateAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", newEmail: null);

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Email", PendingExportAttributeChangeType.Update, (string?)null) }),
            "the account is told the attribute holds nothing, as a synchronisation-driven clear would tell it");
    }

    [Test]
    public async Task DeltaSync_ValueChangedBackToWhatTheAccountHolds_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", "alice@new.local");
        await ChangeEmailAsync(ctx, "Alice", "alice@old.local");

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "net of both edits, the account is already right");
    }

    [Test]
    public async Task DeltaSync_EditToOnePerson_LeavesEveryoneElsesAccountAloneAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", "alice@new.local");

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(ChangesFor(ctx, "Bob"), Is.Empty);
    }

    [Test]
    public async Task DeltaSync_RecordedChangesNamingAnAttributeThatNoLongerExists_DeliversTheRestAsync()
    {
        // The attribute was deleted after the change was recorded: that change is skipped (and logged), the run carries on
        // and delivers the change to the attribute that survives.
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", "alice@new.local");
        var alice = MetaverseObjectOf(ctx, "Alice");
        var recorded = MetaverseObjectUnsynchronisedChanges.FromJson(alice.UnsynchronisedChangesJson)!;
        recorded.AttributeIds.Add(987654);
        recorded.Removals.Add(new MetaverseObjectUnsynchronisedRemoval { AttributeId = 987654, StringValue = "gone" });
        alice.UnsynchronisedChangesJson = recorded.ToJson();

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Email", PendingExportAttributeChangeType.Update, (string?)"alice@new.local") }));
            Assert.That(activity.RunProfileExecutionItems.Where(i => i.ErrorType is { } e && e != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
        }
    }

    [Test]
    public async Task DeltaSync_RecordedChangesThatCannotBeRead_ReviewsScopeAloneAndClearsThemAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        var alice = MetaverseObjectOf(ctx, "Alice");
        alice.ScopeReviewPending = true;
        alice.UnsynchronisedChangesJson = "{ not json";

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportsFor(ctx.Directory), Is.Empty);
            Assert.That(alice.ScopeReviewPending, Is.False);
            Assert.That(alice.UnsynchronisedChangesJson, Is.Null);
            Assert.That(activity.Status, Is.Not.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.WarningMessage, Does.Contain("1 Metaverse Object recorded changes made outside synchronisation that could not be read"),
                "the record is cleared with the flag, so the Activity is the only place left to say that its changes were not delivered");
            Assert.That(activity.WarningMessage, Does.Contain("Edit the affected object again"));
        }
    }

    [Test]
    public async Task DeltaSync_RecordedChangesThatCannotBeReadOnSeveralObjects_RaisesOneWarningCountingThemAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var person = MetaverseObjectOf(ctx, name);
            person.ScopeReviewPending = true;
            person.UnsynchronisedChangesJson = "{ not json";
        }

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        var lines = (activity.WarningMessage ?? string.Empty).Split('\n');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines.Count(line => line.Contains("could not be read")), Is.EqualTo(1), "one warning for the drain, not one per object");
            Assert.That(activity.WarningMessage, Does.Contain("2 Metaverse Objects recorded changes made outside synchronisation that could not be read"));
        }
    }

    [Test]
    public async Task DeltaSync_RecordedChangesReadable_RaisesNoUnreadableWarningAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        var alice = MetaverseObjectOf(ctx, "Alice");
        alice.ScopeReviewPending = true;

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(activity.WarningMessage ?? string.Empty, Does.Not.Contain("could not be read"));
    }

    #endregion

    #region A removed value is removed, and nothing else is

    [Test]
    public async Task DeltaSync_ValueRemovedFromAMultiValuedAttribute_StagesARemovalOfThatValueAloneAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        RemoveAlias(ctx, "Alice", AliceAliasRemoved);

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Aliases", PendingExportAttributeChangeType.Remove, (string?)AliceAliasRemoved) }),
            "exactly the value JIM removed; the value the Directory holds that JIM never managed is left alone");
    }

    [Test]
    public async Task DeltaSync_ValueRemovedThenGivenBack_StagesNothingAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        RemoveAlias(ctx, "Alice", AliceAliasRemoved);
        await AddAliasAsync(ctx, "Alice", AliceAliasRemoved);

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty, "the Metaverse Object holds the value again, so the Directory keeps it");
    }

    [Test]
    public async Task DeltaSync_ValueAddedToAMultiValuedAttribute_StagesAnAddOfThatValueAloneAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        await AddAliasAsync(ctx, "Alice", "alice.smith@corp.local");

        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Aliases", PendingExportAttributeChangeType.Add, (string?)"alice.smith@corp.local") }));
    }

    #endregion

    #region Delivering twice is harmless

    [Test]
    public async Task DeltaSync_ReviewRepeatedBecauseTheClearKeptTheRecord_StagesTheSameChangeOnceAsync()
    {
        // When the object is written after a review loads it, the clear keeps the flag and the record and the next review
        // delivers the same change again; the Pending Export the first review queued must absorb it.
        var ctx = await SetUpAsync(directoryAccounts: true);
        await ChangeEmailAsync(ctx, "Alice", "alice@new.local");
        var alice = MetaverseObjectOf(ctx, "Alice");
        var recorded = alice.UnsynchronisedChangesJson;
        await RunUnrelatedDeltaSyncAsync(ctx);

        alice.ScopeReviewPending = true;
        alice.UnsynchronisedChangesJson = recorded;
        await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportsFor(ctx.Directory), Has.Count.EqualTo(1), "one Pending Export for the account, not two");
            Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Email", PendingExportAttributeChangeType.Update, (string?)"alice@new.local") }));
        }
    }

    [Test]
    public async Task DeltaSync_RemovalDeliveredTwice_StagesTheRemovalOnceAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        RemoveAlias(ctx, "Alice", AliceAliasRemoved);
        var alice = MetaverseObjectOf(ctx, "Alice");
        var recorded = alice.UnsynchronisedChangesJson;
        await RunUnrelatedDeltaSyncAsync(ctx);

        alice.ScopeReviewPending = true;
        alice.UnsynchronisedChangesJson = recorded;
        await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingExportsFor(ctx.Directory), Has.Count.EqualTo(1));
            Assert.That(ChangesFor(ctx, "Alice"), Is.EqualTo(new[] { ("Aliases", PendingExportAttributeChangeType.Remove, (string?)AliceAliasRemoved) }));
        }
    }

    #endregion

    #region Types and rules that cannot deliver anything

    [Test]
    public async Task DeltaSync_ObjectOfATypeNoExportRuleCovers_HasItsFlagClearedWithoutAReviewAsync()
    {
        var ctx = await SetUpAsync(directoryAccounts: true);
        var groupType = await CreateMvObjectTypeAsync("Group");
        var group = new MetaverseObject { Id = Guid.NewGuid(), Type = groupType, Origin = MetaverseObjectOrigin.Internal };
        group.AttributeValues.Add(ValueOf(group, groupType.Attributes.First(a => a.Name == "DisplayName"), "Finance"));
        await Jim.Metaverse.CreateMetaverseObjectAsync(group);
        SyncRepo.SeedMetaverseObject(group);
        Assert.That(group.ScopeReviewPending, Is.True, "arrange: creating the group flagged it");

        var activity = await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.ScopeReviewPending, Is.False);
            Assert.That(group.UnsynchronisedChangesJson, Is.Null);
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "the group was cleared without being loaded or reviewed");
        }
    }

    [Test]
    public async Task DeltaSync_PersonCreatedWhileTheOnlyExportRuleIsDisabled_IsProvisionedOnceTheRuleIsEnabledAsync()
    {
        // A disabled rule provisions and deprovisions nothing, so it covers no type: the person's flag is cleared
        // without a review. Enabling the rule flags the whole type again (#1925), which is what provisions the person.
        var ctx = await SetUpAsync(directoryAccounts: false, provisioning: true, ruleEnabled: false);
        var jay = await CreatePersonAsync(ctx, "E9", "Jay Admin");

        await RunUnrelatedDeltaSyncAsync(ctx);
        Assert.That(jay.ScopeReviewPending, Is.False, "cleared without a review while no enabled rule covers the type");
        Assert.That(PendingExportsFor(ctx.Directory), Is.Empty);

        ctx.ExportRule!.Enabled = true;
        await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ctx.ExportRule, ctx.Administrator);
        await RunUnrelatedDeltaSyncAsync(ctx);

        Assert.That(CreatesFor(ctx), Is.EquivalentTo(new[] { "Alice", "Bob", "Jay Admin" }));
    }

    #endregion

    #region A drain that cannot clear stops

    [Test]
    public async Task DeltaSync_EveryFlagInAFullBatchIsKept_StopsInsteadOfReviewingTheSameBatchAgainAsync()
    {
        // The clear keeps the flag of an object written since it was loaded. If it kept every flag in a full batch, the
        // next fetch would return the same batch, and the drain would never finish.
        var syncRepository = new KeepEveryFlagSyncRepository();
        SyncRepo = syncRepository;
        Jim = new JimApplication(MetaverseSaveBypassingRepositoryProxy.Create(Repository), syncRepository: SyncRepo);
        var ctx = await SetUpAsync(directoryAccounts: false);
        for (var n = 0; n < 501; n++)
            SyncRepo.SeedMetaverseObject(new MetaverseObject { Id = Guid.NewGuid(), Type = ctx.MvType, ScopeReviewPending = true });

        await RunUnrelatedDeltaSyncAsync(ctx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(syncRepository.ClearCalls, Is.EqualTo(1), "the second fetch returned the batch just reviewed, so the drain stopped");
            Assert.That(SyncRepo.MetaverseObjects.Values.Count(mvo => mvo.ScopeReviewPending), Is.EqualTo(501), "and left them flagged for the next run");
        }
    }

    /// <summary>A sync store whose guarded clear keeps every flag, as when each object has been written since it was loaded.</summary>
    private sealed class KeepEveryFlagSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public int ClearCalls { get; private set; }

        public override Task<bool> ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyDictionary<Guid, uint> seenRowVersions, DateTime? exportRulesReadWatermark)
        {
            ClearCalls++;
            if (ClearCalls > 5)
                throw new InvalidOperationException("The drain did not stop.");
            return Task.FromResult(true);
        }
    }

    #endregion

    #region Topology

    private sealed record Context(
        ConnectedSystem Hr,
        ConnectedSystem Directory,
        ConnectedSystemObjectType DirectoryType,
        MetaverseObjectType MvType,
        MetaverseAttribute MvEmployeeId,
        MetaverseAttribute MvDisplayName,
        MetaverseAttribute MvEmail,
        MetaverseAttribute MvAliases,
        SyncRule? ExportRule,
        MetaverseObject Administrator);

    /// <param name="directoryAccounts">Seed a joined, current Directory account for Alice and Bob.</param>
    /// <param name="provisioning">Whether the export rule provisions.</param>
    /// <param name="ruleEnabled">Whether the export rule starts enabled.</param>
    /// <param name="withExportRule">False to build the topology with no export rule at all.</param>
    private async Task<Context> SetUpAsync(bool directoryAccounts, bool provisioning = false, bool ruleEnabled = true, bool withExportRule = true)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmail = NewMetaverseAttribute("Email", AttributePlurality.SingleValued, mvType);
        var mvAliases = NewMetaverseAttribute("Aliases", AttributePlurality.MultiValued, mvType);
        DbContext.MetaverseAttributes.AddRange(mvEmail, mvAliases);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvEmail);
        mvType.Attributes.Add(mvAliases);
        foreach (var attribute in mvType.Attributes)
            SyncRepo.SeedMetaverseAttributeName(attribute.Id, attribute.Name);

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", Attributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User", Attributes());

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);

        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        AddImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        var directoryEmployeeId = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeId, ConnectedSystemAttributeId = directoryEmployeeId.Id } }
        });

        SyncRule? exportRule = null;
        if (withExportRule)
        {
            exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: provisioning);
            ConfigureExportRule(exportRule, directoryType, mvEmployeeId, mvDisplayName, mvEmail, mvAliases);
            exportRule.Enabled = ruleEnabled;
        }

        await DbContext.SaveChangesAsync();

        SeedCso(hr, hrType, "E1", "Alice", null, []);
        SeedCso(hr, hrType, "E2", "Bob", null, []);
        if (directoryAccounts)
        {
            SeedCso(directory, directoryType, "E1", "Alice", "alice@old.local", [AliceAliasKept, AliceAliasRemoved, AliasOnlyTheDirectoryHolds]);
            SeedCso(directory, directoryType, "E2", "Bob", "bob@old.local", ["bob@corp.local"]);
        }

        await RunFullSyncAsync(hr);
        if (directoryAccounts)
        {
            // HR does not supply Email or Aliases: they are held on the Metaverse Object as earlier edits left them, the
            // same as the Directory account holds them, so the arrangement starts with nothing to export.
            foreach (var (name, email, aliases) in new[] { ("Alice", "alice@old.local", new[] { AliceAliasKept, AliceAliasRemoved }), ("Bob", "bob@old.local", new[] { "bob@corp.local" }) })
            {
                var person = SyncRepo.MetaverseObjects.Values.Single(mvo => mvo.Type?.Id == mvType.Id &&
                    mvo.AttributeValues.Any(av => av.AttributeId == mvDisplayName.Id && av.StringValue == name));
                person.AttributeValues.Add(ValueOf(person, mvEmail, email));
                foreach (var alias in aliases)
                    person.AttributeValues.Add(ValueOf(person, mvAliases, alias));
            }

            await RunFullSyncAsync(directory);
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == directory.Id).Select(c => c.MetaverseObjectId),
                Has.All.Not.Null, "arrange: every Directory account is joined");
            Assert.That(PendingExportsFor(directory), Is.Empty, "arrange: every joined account is current");
        }

        var administrator = await NewAdministratorAsync();
        SyncRule? trackedExportRule = null;
        if (exportRule != null)
        {
            trackedExportRule = await Jim.ConnectedSystems.GetSyncRuleAsync(exportRule.Id)
                ?? throw new InvalidOperationException("arrange: the export rule reloads");
            SyncRepo.SeedSyncRule(trackedExportRule);
        }

        return new Context(hr, directory, directoryType, mvType, mvEmployeeId, mvDisplayName, mvEmail, mvAliases, trackedExportRule, administrator);
    }

    private static MetaverseAttribute NewMetaverseAttribute(string name, AttributePlurality plurality, MetaverseObjectType type) => new()
    {
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = plurality,
        MetaverseObjectTypes = new List<MetaverseObjectType> { type },
        PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
    };

    private static void ConfigureExportRule(SyncRule rule, ConnectedSystemObjectType directoryType, MetaverseAttribute employeeId,
        MetaverseAttribute displayName, MetaverseAttribute email, MetaverseAttribute aliases)
    {
        // Enforce State is off: the Directory is allowed to hold values JIM never managed, which is the case a removal
        // must not disturb.
        rule.EnforceState = false;
        AddExportFlow(rule, directoryType, "EmployeeId", employeeId);
        AddExportFlow(rule, directoryType, "DisplayName", displayName);
        AddExportFlow(rule, directoryType, "Email", email);
        AddExportFlow(rule, directoryType, "Aliases", aliases);
    }

    /// <summary>An unscoped export rule that provisions, saved through the application layer as an administrator would.</summary>
    private async Task CreateProvisioningExportRuleAsync(Context ctx)
    {
        var rule = new SyncRule
        {
            Name = "Directory Provisioning",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = ctx.Directory.Id,
            ConnectedSystem = await DbContext.ConnectedSystems.SingleAsync(cs => cs.Id == ctx.Directory.Id),
            ConnectedSystemObjectTypeId = ctx.DirectoryType.Id,
            ConnectedSystemObjectType = await DbContext.ConnectedSystemObjectTypes.SingleAsync(t => t.Id == ctx.DirectoryType.Id),
            MetaverseObjectTypeId = ctx.MvType.Id,
            MetaverseObjectType = await DbContext.MetaverseObjectTypes.SingleAsync(t => t.Id == ctx.MvType.Id),
            ProvisionToConnectedSystem = true,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
        ConfigureExportRule(rule, ctx.DirectoryType, ctx.MvEmployeeId, ctx.MvDisplayName, ctx.MvEmail, ctx.MvAliases);
        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, ctx.Administrator), Is.True, "arrange: the rule saves");
        SyncRepo.SeedSyncRule(await Jim.ConnectedSystems.GetSyncRuleAsync(rule.Id) ?? throw new InvalidOperationException("the rule saves"));
    }

    /// <summary>The initiating user the audited configuration paths need.</summary>
    private async Task<MetaverseObject> NewAdministratorAsync()
    {
        SettleSynchronisationChanges();

        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();
        return administrator;
    }

    /// <summary>
    /// Settles what the synchronisation processors modified on instances shared with the DbContext, so a configuration
    /// save does not try to write them (the pattern of <c>StalePendingExportLifecycleWorkflowTests</c>).
    /// </summary>
    private void SettleSynchronisationChanges()
    {
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = entry.Entity is SyncRule or SyncRuleMapping or SyncRuleMappingSource or ConnectedSystem
                or ConnectedSystemObjectType or ConnectedSystemObjectTypeAttribute or ConnectedSystemRunProfile
                ? EntityState.Unchanged
                : EntityState.Detached;
    }

    private static List<ConnectedSystemObjectTypeAttribute> Attributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Email", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Aliases", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
    ];

    private static void AddImportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private static void AddExportFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute source)
    {
        var target = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
    }

    private void SeedCso(ConnectedSystem system, ConnectedSystemObjectType type, string employeeId, string displayName, string? email, string[] aliases)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = type.Id,
            Type = type,
            ConnectedSystem = SyncRepo.ConnectedSystems[system.Id],
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        cso.ExternalIdAttributeId = externalId.Id;
        AddValue(cso, externalId, guidValue: Guid.NewGuid());
        AddValue(cso, type.Attributes.Single(a => a.Name == "EmployeeId"), employeeId);
        AddValue(cso, type.Attributes.Single(a => a.Name == "DisplayName"), displayName);
        if (email != null)
            AddValue(cso, type.Attributes.Single(a => a.Name == "Email"), email);
        foreach (var alias in aliases)
            AddValue(cso, type.Attributes.Single(a => a.Name == "Aliases"), alias);
        SyncRepo.SeedConnectedSystemObject(cso);
    }

    private static void AddValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute, string? stringValue = null, Guid? guidValue = null) =>
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute,
            StringValue = stringValue, GuidValue = guidValue
        });

    private static MetaverseObjectAttributeValue ValueOf(MetaverseObject owner, MetaverseAttribute attribute, string text) => new()
    {
        Id = Guid.NewGuid(), MetaverseObject = owner, Attribute = attribute, AttributeId = attribute.Id, StringValue = text
    };

    #endregion

    #region Writes outside synchronisation

    /// <summary>
    /// Creates a person as a first sign-in or the portal would, and shares the instance with the sync store, which in
    /// production reads the same row.
    /// </summary>
    private async Task<MetaverseObject> CreatePersonAsync(Context ctx, string employeeId, string displayName)
    {
        var person = new MetaverseObject { Id = Guid.NewGuid(), Type = ctx.MvType, Origin = MetaverseObjectOrigin.Internal };
        person.AttributeValues.Add(ValueOf(person, ctx.MvEmployeeId, employeeId));
        person.AttributeValues.Add(ValueOf(person, ctx.MvDisplayName, displayName));
        await Jim.Metaverse.CreateMetaverseObjectAsync(person);
        SyncRepo.SeedMetaverseObject(person);
        return person;
    }

    /// <summary>Replaces (or, with null, clears) a person's single-valued Email, as a sign-in refresh or an edit does.</summary>
    private async Task ChangeEmailAsync(Context ctx, string name, string? newEmail)
    {
        var person = MetaverseObjectOf(ctx, name);
        var current = person.AttributeValues.Single(av => av.AttributeId == ctx.MvEmail.Id);
        var removal = current.CopyFor(person);

        if (newEmail == null)
        {
            person.AttributeValues.Remove(current);
            await Jim.Metaverse.UpdateMetaverseObjectAsync(person, additions: [], removals: [removal], initiatedByType: ActivityInitiatorType.User);
            return;
        }

        current.StringValue = newEmail;
        await Jim.Metaverse.UpdateMetaverseObjectAsync(person, additions: [current], removals: [removal], initiatedByType: ActivityInitiatorType.User);
    }

    private void RemoveAlias(Context ctx, string name, string alias)
    {
        var person = MetaverseObjectOf(ctx, name);
        var value = person.AttributeValues.Single(av => av.AttributeId == ctx.MvAliases.Id && av.StringValue == alias);
        person.AttributeValues.Remove(value);
        Jim.Metaverse.UpdateMetaverseObjectAsync(person, additions: [], removals: [value], initiatedByType: ActivityInitiatorType.User).GetAwaiter().GetResult();
    }

    private async Task AddAliasAsync(Context ctx, string name, string alias)
    {
        var person = MetaverseObjectOf(ctx, name);
        var value = ValueOf(person, ctx.MvAliases, alias);
        person.AttributeValues.Add(value);
        await Jim.Metaverse.UpdateMetaverseObjectAsync(person, additions: [value], removals: [], initiatedByType: ActivityInitiatorType.User);
    }

    #endregion

    #region Reading the outcome

    /// <summary>A person's Metaverse Object, by the Display Name it holds.</summary>
    private MetaverseObject MetaverseObjectOf(Context ctx, string displayName) => SyncRepo.MetaverseObjects.Values
        .Single(mvo => mvo.Type?.Id == ctx.MvType.Id &&
            mvo.AttributeValues.Any(av => av.AttributeId == ctx.MvDisplayName.Id && av.StringValue == displayName));

    /// <summary>
    /// How an item names a person's Metaverse Object. The fixture's attribute is "DisplayName" rather than the built-in
    /// "Display Name", so the object has no name of its own here and is named by its id instead.
    /// </summary>
    private string ItemNameOf(Context ctx, string displayName) => MetaverseObjectOf(ctx, displayName).NameOrId;

    private List<PendingExport> PendingExportsFor(ConnectedSystem system) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == system.Id).ToList();

    /// <summary>The people a Create is staged for in the Directory, by the Display Name it would write.</summary>
    private List<string?> CreatesFor(Context ctx) => PendingExportsFor(ctx.Directory)
        .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
        .Select(pe => pe.AttributeValueChanges.SingleOrDefault(c => c.Attribute?.Name == "DisplayName")?.StringValue)
        .ToList();

    /// <summary>
    /// What is staged for a person's Directory account: the attribute, the kind of change and the value, in the order
    /// staged. Empty when nothing is queued for the account.
    /// </summary>
    private List<(string? Attribute, PendingExportAttributeChangeType ChangeType, string? Value)> ChangesFor(Context ctx, string displayName)
    {
        var account = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == ctx.Directory.Id &&
            c.AttributeValues.Any(av => av.Attribute?.Name == "DisplayName" && av.StringValue == displayName));
        return PendingExportsFor(ctx.Directory)
            .Where(pe => pe.ConnectedSystemObjectId == account.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Select(change => (change.Attribute?.Name, change.ChangeType, change.StringValue))
            .ToList();
    }

    #endregion

    #region Running synchronisations

    /// <summary>
    /// A Delta Synchronisation of HR, which has nothing new to process: the run that delivers a write made outside
    /// synchronisation is not the run of any system the write concerns.
    /// </summary>
    private async Task<Activity> RunUnrelatedDeltaSyncAsync(Context ctx)
    {
        await Task.Delay(5);
        SyncRepo.ConnectedSystems[ctx.Hr.Id].LastSyncCompletedAt = DateTime.UtcNow;
        return await RunDeltaSyncAsync(ctx.Hr);
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    private async Task<Activity> RunDeltaSyncAsync(ConnectedSystem connectedSystem)
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();
        return activity;
    }

    #endregion
}
