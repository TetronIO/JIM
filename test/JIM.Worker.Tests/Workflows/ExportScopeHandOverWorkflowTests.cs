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
/// An export Synchronisation Rule's scope exit hands the account to another enabled export rule on the same Connected
/// System and Object Type that still covers the identity, rather than deprovisioning it (#2822). Two rules, "AD Staff
/// Accounts" and "AD Contractor Accounts", provision into one AD <c>user</c> type, scoped on the identity's Type.
/// </summary>
[TestFixture]
public class ExportScopeHandOverWorkflowTests : WorkflowTestBase
{
    private const string Staff = "Staff";
    private const string Contractor = "Contractor";
    private const string Intern = "Intern";

    [Test]
    public async Task FullSync_JoinerUnderDisconnect_KeepsTheAccountTheCoveringRuleProvisionedAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Disconnect, Contractor);

        await RunHrFullSyncAsync(arrangement, "Joiner");
        await RunHrFullSyncAsync(arrangement, "Next run");

        var janeMvoId = JaneMvoId();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AdAccountsFor(arrangement, janeMvoId), Has.Count.EqualTo(1),
                "the Staff rule, which the joiner is not in, must not disconnect the account the Contractor rule provisioned");
            Assert.That(AdPendingExportsFor(arrangement, janeMvoId).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Create }), "one Create, never a second");
        }
    }

    [Test]
    public async Task FullSync_MoverUnderDelete_HandsTheAccountToTheCoveringRuleAndStagesNoDeleteAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Delete, Contractor);
        var account = await ProvisionAndConfirmAsync(arrangement);

        await MoveJaneToAsync(arrangement, Staff);
        var activity = await RunHrFullSyncAsync(arrangement, "Move to Staff");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Is.Empty, "the account is not deleted while the Staff rule covers Jane");
            Assert.That(account.MetaverseObjectId, Is.EqualTo(JaneMvoId()), "the join stays");
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.StaffRule.Id), "the Staff rule now manages the account");
            Assert.That(AdAccountsFor(arrangement, JaneMvoId()), Has.Count.EqualTo(1), "no second account is provisioned");
            var handOver = HandOversIn(activity).SingleOrDefault();
            Assert.That(handOver, Is.Not.Null, "the run records the hand-over");
            Assert.That(handOver?.DetailMessage, Does.Contain("AD Contractor Accounts").And.Contain("AD Staff Accounts"));
        }
    }

    [Test]
    public async Task FullSync_MoverUnderDisconnect_KeepsTheAccountJoinedAndProvisionsNoSecondAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Disconnect, Contractor);
        var account = await ProvisionAndConfirmAsync(arrangement);

        await MoveJaneToAsync(arrangement, Staff);
        await RunHrFullSyncAsync(arrangement, "Move to Staff");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.MetaverseObjectId, Is.EqualTo(JaneMvoId()), "the account is not left unmanaged");
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.StaffRule.Id));
            Assert.That(AdAccountsFor(arrangement, JaneMvoId()), Has.Count.EqualTo(1));
            Assert.That(AdPendingExportsFor(arrangement, JaneMvoId()).Where(pe => pe.ChangeType == PendingExportChangeType.Create), Is.Empty,
                "the Staff rule does not try to provision a second account");
        }
    }

    [Test]
    public async Task FullSync_TwoStepMoverUnderDelete_CancelsTheStaleDeleteWhenTheCoveringRuleTakesOverAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Delete, Contractor);
        var account = await ProvisionAndConfirmAsync(arrangement);

        await MoveJaneToAsync(arrangement, Intern);
        await RunHrFullSyncAsync(arrangement, "Gap");
        Assert.That(PendingDeletesFor(account), Has.Count.EqualTo(1), "arrange: out of every rule's scope, the leaver's Delete is staged as today");

        await MoveJaneToAsync(arrangement, Staff);
        await RunHrFullSyncAsync(arrangement, "Into Staff");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Is.Empty, "the stale Delete is cancelled once a rule covers Jane again before export");
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(arrangement.StaffRule.Id));
        }
    }

    [Test]
    public async Task FullSync_MoverAfterTheDeleteWasExported_LeavesTheExportedDeleteToFinishAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Delete, Contractor);
        var account = await ProvisionAndConfirmAsync(arrangement);
        await MoveJaneToAsync(arrangement, Intern);
        await RunHrFullSyncAsync(arrangement, "Gap");
        var delete = PendingDeletesFor(account).Single();
        delete.Status = PendingExportStatus.Exported;

        await MoveJaneToAsync(arrangement, Staff);
        await RunHrFullSyncAsync(arrangement, "Into Staff");

        Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.Id == delete.Id && pe.ChangeType == PendingExportChangeType.Delete).ToList(), Has.Count.EqualTo(1),
            "the account is already gone or going from AD; the exported Delete stands for the confirming import to finish");
    }

    [Test]
    public async Task FullSync_JoinMadeByAnImportRule_IsNotRepointedAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Delete, Contractor, withAdImportRule: true);
        var account = await ProvisionAndConfirmAsync(arrangement);
        var adImport = arrangement.AdImportRule!;
        account.RecordJoin(ConnectedSystemObjectJoinMethod.InboundMatching, adImport, DateTime.UtcNow);

        await MoveJaneToAsync(arrangement, Staff);
        await RunHrFullSyncAsync(arrangement, "Move to Staff");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PendingDeletesFor(account), Is.Empty, "the Staff rule still covers Jane, so nothing is deprovisioned");
            Assert.That(account.JoinSyncRuleId, Is.EqualTo(adImport.Id), "a join an import rule made is the import rule's to describe");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfAMoverACoveringRuleTakesOver_ProposesNoDeprovisioningAsync()
    {
        var arrangement = await ArrangeAsync(OutboundDeprovisionAction.Delete, Contractor);
        var account = await ProvisionAndConfirmAsync(arrangement);
        await MoveJaneToAsync(arrangement, Staff);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arrangement.Hr.Id, arrangement.JaneHrObject.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Outbound.ProposedExports.Where(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.Empty,
                "the run stages no Delete, so the preview proposes none");
            Assert.That(preview.OutboundDecisions?.Entries.Where(e => e.Kind == OutboundPreviewEntryKind.Deprovisioning), Is.Empty);
            Assert.That(preview.OutboundDecisions?.HandOvers.Select(h => h.ConnectedSystemObjectId), Is.EqualTo(new[] { account.Id }),
                "the preview reports the hand-over the run records");
        }
    }

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystemObjectType HrType, ConnectedSystemObject JaneHrObject,
        ConnectedSystem Ad, ConnectedSystemObjectType AdType, MetaverseObjectType MvType, SyncRule StaffRule, SyncRule ContractorRule,
        SyncRule? AdImportRule);

    /// <summary>
    /// HR projects Jane with the given Type. Both export rules provision into AD <c>user</c>, flow DisplayName and EmployeeId,
    /// and carry the given Outbound Deprovision Action; "AD Staff Accounts" covers Type Staff and "AD Contractor Accounts" Type
    /// Contractor. An Intern is in neither. With <paramref name="withAdImportRule"/>, AD also has an import rule that
    /// projects nothing, so a join can be recorded as an import rule's.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(OutboundDeprovisionAction deprovisionAction, string janeType, bool withAdImportRule = false)
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
            Id = Guid.NewGuid(), AttributeId = employeeType.Id, Attribute = employeeType, StringValue = janeType, ConnectedSystemObject = jane
        });

        var ad = await CreateConnectedSystemAsync("AD");
        var adType = await CreateCsoTypeAsync(ad.Id, "user");
        var staffRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Staff Accounts", deprovisionAction: deprovisionAction);
        var contractorRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Contractor Accounts", deprovisionAction: deprovisionAction);
        foreach (var (rule, type) in new[] { (staffRule, Staff), (contractorRule, Contractor) })
        {
            AddExportFlow(rule, adType, "DisplayName", mvDisplayName);
            AddExportFlow(rule, adType, "EmployeeId", mvEmployeeId);
            ScopeToType(rule, mvTypeAttribute, type);
        }
        var adImport = withAdImportRule
            ? await CreateImportSyncRuleAsync(ad.Id, adType, mvType, "AD Import", enableProjection: false)
            : null;
        await DbContext.SaveChangesAsync();

        return new Arrangement(hr, hrType, jane, ad, adType, mvType, staffRule, contractorRule, adImport);
    }

    /// <summary>
    /// Runs the first synchronisation, which provisions Jane's account, then stands it up as exported and confirmed: the
    /// account exists in AD, and its Create is no longer queued.
    /// </summary>
    private async Task<ConnectedSystemObject> ProvisionAndConfirmAsync(Arrangement arrangement)
    {
        await RunHrFullSyncAsync(arrangement, "Joiner");
        var account = AdAccountsFor(arrangement, JaneMvoId()).Single();
        account.Status = ConnectedSystemObjectStatus.Normal;
        await SyncRepo.DeletePendingExportsAsync(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == account.Id).ToList());
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
            .SelectMany(item => item.SyncOutcomes) // every outcome, nested ones included
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
