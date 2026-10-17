// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Export matching that cannot settle on one existing account before provisioning stops the identity for that rule and
/// says so (#2052). Before this, the lowest-id account was joined when several matched, and a rule that could not be
/// worked out was logged and ignored, so the identity was provisioned: a duplicate account with nothing to say why.
/// </summary>
[TestFixture]
public class ProvisioningMatchWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_IdentityMatchesTwoAccounts_RecordsBothAndNeitherJoinsNorProvisionsAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();

        var activity = await RunFullSyncAsync(arranged.Hr);

        var identity = SyncRepo.MetaverseObjects.Values.Single();
        var item = ItemsOf(activity).Single(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.AmbiguousProvisioningMatch);
        var candidates = SyncRepo.MatchCandidates.Where(c => c.ActivityRunProfileExecutionItemId == item.Id).ToList();
        var directoryObjects = SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == arranged.Directory.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(directoryObjects.Select(c => c.Id), Is.EquivalentTo(arranged.Accounts), "nothing is provisioned");
            Assert.That(directoryObjects.All(c => c.MetaverseObjectId == null), Is.True, "neither account is joined");
            Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == arranged.Directory.Id), Is.Empty);
            Assert.That(item.MetaverseObjectId, Is.EqualTo(identity.Id));
            Assert.That(item.ErrorMessage, Does.Contain("2 Connected System Objects in 'Directory'"));
            Assert.That(candidates.Select(c => c.CandidateId), Is.EquivalentTo(arranged.Accounts));
            Assert.That(candidates.All(c => c.CandidateKind == MatchCandidateKind.ConnectedSystemObject), Is.True);
            Assert.That(candidates.All(c => c.ConnectedSystemId == arranged.Directory.Id), Is.True);
            Assert.That(candidates.All(c => c.ObjectMatchingRuleId == arranged.MatchingRule.Id), Is.True);
            Assert.That(candidates.All(c => c.MatchedValue == "EMP001"), Is.True);
        }
    }

    [Test]
    public async Task FullSync_IdentityMatchesOneAccount_JoinsItAndRecordsTheRuleAndValueAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        var admin = SyncRepo.ConnectedSystemObjects[arranged.Accounts[1]];
        admin.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "ADM001";

        await RunFullSyncAsync(arranged.Hr);

        var identity = SyncRepo.MetaverseObjects.Values.Single();
        var account = SyncRepo.ConnectedSystemObjects[arranged.Accounts[0]];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.MetaverseObjectId, Is.EqualTo(identity.Id));
            Assert.That(account.JoinMethod, Is.EqualTo(ConnectedSystemObjectJoinMethod.ExportMatching));
            Assert.That(account.JoinObjectMatchingRuleId, Is.EqualTo(arranged.MatchingRule.Id));
            Assert.That(account.JoinMatchValue, Is.EqualTo("EMP001"));
        }
    }

    [Test]
    public async Task FullSync_ExpressionRuleComputesTheIdentitysValueForOneAccount_JoinsItAsync()
    {
        // The Directory stores Employee IDs in lower case, so the case-sensitive rule computes Upper(cs["EmployeeId"]).
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        arranged.MatchingRule.Sources[0].ConnectedSystemAttribute = null;
        arranged.MatchingRule.Sources[0].ConnectedSystemAttributeId = null;
        arranged.MatchingRule.Sources[0].Expression = "Upper(cs[\"EmployeeId\"])";
        var account = SyncRepo.ConnectedSystemObjects[arranged.Accounts[0]];
        account.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "emp001";
        SyncRepo.ConnectedSystemObjects[arranged.Accounts[1]].AttributeValues
            .Single(av => av.Attribute?.Name == "EmployeeId").StringValue = "adm001";

        var activity = await RunFullSyncAsync(arranged.Hr);

        var identity = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(account.MetaverseObjectId, Is.EqualTo(identity.Id));
            Assert.That(account.JoinObjectMatchingRuleId, Is.EqualTo(arranged.MatchingRule.Id));
            Assert.That(account.JoinMatchValue, Is.EqualTo("EMP001"));
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Count(c => c.ConnectedSystemId == arranged.Directory.Id), Is.EqualTo(2),
                "nothing is provisioned beside the account it joined");
        }
    }

    [Test]
    public async Task FullSync_MatchingRuleCannotBeEvaluated_RecordsTheErrorAndDoesNotProvisionAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        arranged.MatchingRule.Sources[0].ConnectedSystemAttribute = null;
        arranged.MatchingRule.Sources[0].ConnectedSystemAttributeId = null;

        var activity = await RunFullSyncAsync(arranged.Hr);

        var item = ItemsOf(activity).SingleOrDefault(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item, Is.Not.Null);
            Assert.That(item?.ErrorMessage, Does.Contain("provisioned"));
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Count(c => c.ConnectedSystemId == arranged.Directory.Id), Is.EqualTo(2),
                "a rule that cannot be worked out must not let the identity be provisioned as a duplicate");
        }
    }

    /// <summary>
    /// #2323: a schema refresh made the Directory's Employee ID Text, and no import has read it since, so the account
    /// still holds it as the Number it was imported as. The lookup compares the Text column and finds nothing; the
    /// account may be the identity's own, so provisioning would create a second one. The identity is stopped with the
    /// error instead, and is matched once a Full Import has read the attribute as its new type.
    /// </summary>
    [Test]
    public async Task FullSync_OnlyAccountHoldsTheMatchAttributeUnderAnotherDataType_RecordsTheErrorAndNeitherJoinsNorProvisionsAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        var held = HoldEmployeeIdAsNumber(arranged.Accounts[0], 1);
        EmployeeIdOf(arranged.Accounts[1]).StringValue = "ADM001";

        var activity = await RunFullSyncAsync(arranged.Hr);

        var identity = SyncRepo.MetaverseObjects.Values.Single();
        var item = ItemsOf(activity).SingleOrDefault(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed);
        var directoryObjects = SyncRepo.ConnectedSystemObjects.Values.Where(c => c.ConnectedSystemId == arranged.Directory.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(directoryObjects.Select(c => c.Id), Is.EquivalentTo(arranged.Accounts), "nothing is provisioned");
            Assert.That(directoryObjects.All(c => c.MetaverseObjectId == null), Is.True, "nothing is joined");
            Assert.That(SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemId == arranged.Directory.Id), Is.Empty);
            Assert.That(item?.MetaverseObjectId, Is.EqualTo(identity.Id));
            Assert.That(item?.ErrorMessage, Does.Contain("'Directory'").And.Contain(held.Id.ToString()).And.Contain("held as Number"));
        }
    }

    [Test]
    public async Task FullSync_IdentitysAccountHoldsTheCurrentTypeWhileAnotherIsHeldUnderAnotherDataType_JoinsItAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        HoldEmployeeIdAsNumber(arranged.Accounts[1], 1);

        var activity = await RunFullSyncAsync(arranged.Hr);

        var identity = SyncRepo.MetaverseObjects.Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(SyncRepo.ConnectedSystemObjects[arranged.Accounts[0]].MetaverseObjectId, Is.EqualTo(identity.Id));
        }
    }

    [Test]
    public async Task FullSync_NoAccountHoldsTheValueAndNoneIsHeldUnderAnotherDataType_ProvisionsAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();
        EmployeeIdOf(arranged.Accounts[0]).StringValue = "EMP002";
        EmployeeIdOf(arranged.Accounts[1]).StringValue = "ADM001";

        var activity = await RunFullSyncAsync(arranged.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ItemsOf(activity).Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet), Is.Empty);
            Assert.That(SyncRepo.ConnectedSystemObjects.Values.Count(c => c.ConnectedSystemId == arranged.Directory.Id), Is.EqualTo(3),
                "the identity is provisioned an account of its own");
        }
    }

    private ConnectedSystemObjectAttributeValue EmployeeIdOf(Guid accountId) =>
        SyncRepo.ConnectedSystemObjects[accountId].AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");

    /// <summary>The account's Employee ID as a schema refresh leaves it after redefining the attribute from Number to Text.</summary>
    private ConnectedSystemObject HoldEmployeeIdAsNumber(Guid accountId, int employeeId)
    {
        var value = EmployeeIdOf(accountId);
        value.StringValue = null;
        value.IntValue = employeeId;
        return SyncRepo.ConnectedSystemObjects[accountId];
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_IdentityMatchesTwoAccounts_ReportsTheErrorWithBothAccountsAsync()
    {
        var arranged = await ArrangeIdentityMatchingTwoAccountsAsync();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arranged.Hr.Id, arranged.HrObject.Id);

        var error = preview.Errors.Single(e => e.Code == SyncPreviewMessageCode.AmbiguousProvisioningMatch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Detail, Does.Contain("would neither join it to one nor provision it"));
            Assert.That(error.MatchCandidates.Select(c => c.CandidateId), Is.EquivalentTo(arranged.Accounts));
            Assert.That(preview.Outbound.ProposedExports.Where(pe => pe.ConnectedSystemId == arranged.Directory.Id), Is.Empty);
        }
    }

    private sealed record Arranged(
        ConnectedSystem Hr, ConnectedSystemObject HrObject, ConnectedSystem Directory, ObjectMatchingRule MatchingRule, List<Guid> Accounts);

    /// <summary>
    /// HR projects Jane Smith with Employee ID EMP001; the Directory already holds two unjoined accounts carrying it, and
    /// its export rule provisions, matching on Employee ID first.
    /// </summary>
    private async Task<Arranged> ArrangeIdentityMatchingTwoAccountsAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        hrImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = hrImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id } }
        });

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryEmployeeIdAttr = directoryType.Attributes.First(a => a.Name == "EmployeeId");
        var exportRule = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export");
        exportRule.ConnectedSystem = directory;
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = directoryEmployeeIdAttr,
            TargetConnectedSystemAttributeId = directoryEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvEmployeeIdAttr, MetaverseAttributeId = mvEmployeeIdAttr.Id } }
        });
        var matchingRule = new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            SyncRule = exportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeIdAttr, ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id }]
        };
        exportRule.ObjectMatchingRules.Add(matchingRule);
        await DbContext.SaveChangesAsync();

        var account = await CreateCsoAsync(directory.Id, directoryType, "jsmith", "EMP001");
        var adminAccount = await CreateCsoAsync(directory.Id, directoryType, "jsmith-admin", "EMP001");
        var hrObject = await CreateCsoAsync(hr.Id, hrType, "Jane Smith", "EMP001");
        return new Arranged(hr, hrObject, directory, matchingRule, [account.Id, adminAccount.Id]);
    }

    private List<ActivityRunProfileExecutionItem> ItemsOf(Activity activity) =>
        SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .ToList();

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
