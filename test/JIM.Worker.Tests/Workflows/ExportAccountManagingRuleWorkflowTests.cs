// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The export Synchronisation Rule that manages an account decides whether it exists (#2826). "AD Base Accounts" provisions
/// Jane's AD <c>user</c> while she is Staff; "AD Manager Attributes" does not provision and only adds an attribute. A rule
/// that does not provision never keeps an account alive after the rule managing it stops covering it, and its own
/// Deprovisioning Action never applies to an account another rule manages.
/// </summary>
[TestFixture]
public class ExportAccountManagingRuleWorkflowTests : WorkflowTestBase
{
    private const string Staff = "Staff";
    private const string Intern = "Intern";

    [Test]
    public async Task FullSync_ManagingRuleLeavesWhileARuleThatDoesNotProvisionCovers_DeprovisionsByTheManagingRuleAsync()
    {
        // The add-on rule covers Interns: once Jane moves, it is the only rule covering her, as a stale HR flag would leave it.
        var arrangement = await ArrangeAsync(baseAction: OutboundDeprovisionAction.Delete, addOnAction: OutboundDeprovisionAction.Disconnect,
            addOnScope: Intern);
        var account = await ProvisionAndConfirmAsync(arrangement);

        await MoveJaneToAsync(arrangement, Intern);
        var activity = await RunHrFullSyncAsync(arrangement, "Move to Intern");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Has.Count.EqualTo(1),
                "the rule that manages the account no longer covers Jane, and a rule that does not provision cannot keep it");
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.BaseRule.Id), "the add-on rule does not take the account over");
            Assert.That(HandOversIn(activity), Is.Empty);
            Assert.That(AdPendingExportsFor(arrangement, JaneMvoId()).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Delete }),
                "the add-on rule stages nothing for an account its managing rule is deprovisioning");
        }
    }

    [Test]
    public async Task FullSync_BothRulesLeaveAndTheAddOnSaysDelete_OnlyTheManagingRulesDisconnectAppliesAsync()
    {
        var arrangement = await ArrangeAsync(baseAction: OutboundDeprovisionAction.Disconnect, addOnAction: OutboundDeprovisionAction.Delete,
            addOnScope: Staff, addOnEnabled: false);
        var account = await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = true; // the run reads the rule the in-memory repository holds

        await MoveJaneToAsync(arrangement, Intern);
        await RunHrFullSyncAsync(arrangement, "Leaver");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Is.Empty,
                "the add-on rule's Delete does not apply to an account another rule manages; today Delete would beat Disconnect");
            Assert.That(account.MetaverseObjectId, Is.Null, "the managing rule's Disconnect applies");
        }
    }

    [Test]
    public async Task FullSync_AddOnRuleLeavesWhileTheManagingRuleCovers_KeepsTheAccountAsync()
    {
        // The base rule covers everyone. The add-on rule covers Interns and says Delete; Jane moves from Intern to Staff, so it
        // leaves while the managing rule still covers her. The add-on is enabled only once the account exists: until #2826's
        // second layer, two rules covering one account stage nothing for it.
        var arrangement = await ArrangeAsync(baseAction: OutboundDeprovisionAction.Disconnect, addOnAction: OutboundDeprovisionAction.Delete,
            addOnScope: Intern, addOnEnabled: false, baseScope: null);
        var account = await ProvisionAndConfirmAsync(arrangement);
        arrangement.AddOnRule.Enabled = true; // the run reads the rule the in-memory repository holds
        await MoveJaneToAsync(arrangement, Intern);
        await RunHrFullSyncAsync(arrangement, "Into Intern");

        await MoveJaneToAsync(arrangement, Staff);
        await RunHrFullSyncAsync(arrangement, "Promotion");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Is.Empty);
            Assert.That(account.MetaverseObjectId, Is.EqualTo(JaneMvoId()));
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.BaseRule.Id));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfALeaverOnlyARuleThatDoesNotProvisionCovers_ProposesTheManagingRulesDeleteAsync()
    {
        var arrangement = await ArrangeAsync(baseAction: OutboundDeprovisionAction.Delete, addOnAction: OutboundDeprovisionAction.Disconnect,
            addOnScope: Intern);
        await ProvisionAndConfirmAsync(arrangement);
        await MoveJaneToAsync(arrangement, Intern);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arrangement.Hr.Id, arrangement.JaneHrObject.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Outbound.ProposedExports.Select(pe => pe.ChangeType), Is.EqualTo(new[] { PendingExportChangeType.Delete }),
                "the preview proposes what the run stages: the managing rule's Delete, and nothing from the add-on rule");
            Assert.That(preview.OutboundDecisions?.HandOvers, Is.Empty);
        }
    }

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystemObject JaneHrObject, ConnectedSystem Ad, SyncRule BaseRule,
        SyncRule AddOnRule);

    /// <summary>
    /// HR projects Jane as Staff. "AD Base Accounts" provisions into AD <c>user</c> and flows DisplayName; it covers
    /// <paramref name="baseScope"/> (everyone when null). "AD Manager Attributes" does not provision, flows EmployeeId, and
    /// covers <paramref name="addOnScope"/>.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(OutboundDeprovisionAction baseAction, OutboundDeprovisionAction addOnAction, string addOnScope,
        bool addOnEnabled = true, string? baseScope = Staff)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvTypeAttribute = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User",
        [
            new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new ConnectedSystemObjectTypeAttribute { Name = "EmployeeType", Type = AttributeDataType.Text, Selected = true }
        ]);
        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddImportFlow(hrImport, hrType, "EmployeeType", mvTypeAttribute);
        var jane = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");
        var employeeType = hrType.Attributes.Single(a => a.Name == "EmployeeType");
        jane.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = employeeType.Id, Attribute = employeeType, StringValue = Staff, ConnectedSystemObject = jane
        });

        var ad = await CreateConnectedSystemAsync("AD");
        var adType = await CreateCsoTypeAsync(ad.Id, "user");
        var baseRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Base Accounts", deprovisionAction: baseAction);
        AddExportFlow(baseRule, adType, "DisplayName", mvDisplayName);
        if (baseScope != null)
            ScopeToType(baseRule, mvTypeAttribute, baseScope);

        var addOnRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Manager Attributes", enableProvisioning: false,
            deprovisionAction: addOnAction);
        AddExportFlow(addOnRule, adType, "EmployeeId", mvEmployeeId);
        ScopeToType(addOnRule, mvTypeAttribute, addOnScope);
        addOnRule.Enabled = addOnEnabled;
        await DbContext.SaveChangesAsync();

        return new Arrangement(hr, jane, ad, baseRule, addOnRule);
    }

    /// <summary>
    /// Runs the first synchronisation, which provisions Jane's account through the base rule, then stands it up as exported
    /// and confirmed.
    /// </summary>
    private async Task<ConnectedSystemObject> ProvisionAndConfirmAsync(Arrangement arrangement)
    {
        await RunHrFullSyncAsync(arrangement, "Joiner");
        var account = AdAccountsFor(arrangement, JaneMvoId()).Single();
        account.Status = ConnectedSystemObjectStatus.Normal;
        await SyncRepo.DeletePendingExportsAsync(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == account.Id).ToList());
        Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.BaseRule.Id), "arrange: the base rule provisioned and manages the account");
        return account;
    }

    private async Task MoveJaneToAsync(Arrangement arrangement, string type)
    {
        arrangement.JaneHrObject.AttributeValues.Single(v => v.Attribute?.Name == "EmployeeType").StringValue = type;
        await ModifyCsoAsync(arrangement.JaneHrObject);
    }

    private Guid JaneMvoId() =>
        SyncRepo.MetaverseObjects.Values.Single(m => m.AttributeValues.Any(v => v.Attribute?.Name == "EmployeeId" && v.StringValue == "EMP001")).Id;

    private List<ConnectedSystemObject> AdAccountsFor(Arrangement arrangement, Guid mvoId) =>
        SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == arrangement.Ad.Id && c.MetaverseObjectId == mvoId).ToList();

    private List<PendingExport> AdPendingExportsFor(Arrangement arrangement, Guid mvoId) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == arrangement.Ad.Id && pe.SourceMetaverseObjectId == mvoId).ToList();

    private List<PendingExport> PendingDeletesFor(ConnectedSystemObject account) =>
        SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == account.Id && pe.ChangeType == PendingExportChangeType.Delete).ToList();

    private static List<ActivityRunProfileExecutionItemSyncOutcome> HandOversIn(Activity activity) =>
        activity.RunProfileExecutionItems
            .SelectMany(item => item.SyncOutcomes)
            .Where(outcome => outcome.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.HandedToAnotherExportRule)
            .ToList();

    private static void ScopeToType(SyncRule rule, MetaverseAttribute typeAttribute, string value) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    MetaverseAttribute = typeAttribute,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = value,
                    CaseSensitive = true
                }
            ]
        });

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

    private async Task<Activity> RunHrFullSyncAsync(Arrangement arrangement, string profileName)
    {
        var profile = await CreateRunProfileAsync(arrangement.Hr.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var hr = await ReloadEntityAsync(arrangement.Hr);
        var activity = await CreateActivityAsync(hr.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hr, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
