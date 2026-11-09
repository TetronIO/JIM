// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A Metaverse Object holds one Connected System Object per Connected System, and one export Synchronisation Rule
/// manages it (#2820). When two enabled export rules targeting the same Connected System and Object Type both have an
/// identity in scope, each used to stage its own Pending Export for the one object, and the page's flush then failed,
/// losing every object on it. That object now fails on its own, with an execution item naming the rules, and the rest of
/// the page synchronises.
/// </summary>
[TestFixture]
public class OverlappingExportRulesWorkflowTests : WorkflowTestBase
{
    private const string Jane = "EMP001";
    private const string Raj = "EMP002";

    [Test]
    public async Task FullSync_TwoExportRulesCoverOneIdentity_FailsThatObjectAndSynchronisesTheRestAsync()
    {
        var arrangement = await ArrangeAsync(baseScope: null, managersScope: Jane);

        var activity = await RunHrFullSyncAsync(arrangement.Hr);

        var overlaps = activity.RunProfileExecutionItems
            .Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap).ToList();
        var janeMvoId = MvoIdFor(Jane);
        var rajMvoId = MvoIdFor(Raj);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlaps, Has.Count.EqualTo(1), "one execution item for the one identity both rules cover");
            Assert.That(overlaps.Single().MetaverseObjectId, Is.EqualTo(janeMvoId));
            Assert.That(overlaps.Single().ErrorMessage, Does.Contain("AD Base Accounts").And.Contain("AD Manager Accounts"));
            Assert.That(AdPendingExportsFor(arrangement.Ad, janeMvoId), Is.Empty,
                "nothing is staged for an object two rules cover");
            Assert.That(AdPendingExportsFor(arrangement.Ad, rajMvoId).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Create }),
                "an identity only one rule covers is provisioned as normal, on the same page");
        }
    }

    [Test]
    public async Task FullSync_TwoExportRulesCoverAnIdentityWithAnAccount_StagesNothingForItAsync()
    {
        var arrangement = await ArrangeAsync(baseScope: null, managersScope: Jane);
        await RunHrFullSyncAsync(arrangement.Hr, "Bootstrap");
        var janeMvoId = MvoIdFor(Jane);
        var account = await JoinedAccountAsync(arrangement, janeMvoId);
        var pendingBefore = SyncRepo.PendingExports.Keys.ToHashSet();

        var hrCso = SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == arrangement.Hr.Id && c.MetaverseObjectId == janeMvoId);
        hrCso.AttributeValues.Single(v => v.Attribute?.Name == "DisplayName").StringValue = "Jane Smith-Jones";
        await ModifyCsoAsync(hrCso);
        var activity = await RunHrFullSyncAsync(arrangement.Hr, "Rename");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap),
                Is.EqualTo(1));
            Assert.That(SyncRepo.PendingExports.Values.Where(pe => !pendingBefore.Contains(pe.Id) && pe.ConnectedSystemObjectId == account.Id),
                Is.Empty, "no update is staged for an account two rules cover");
        }
    }

    [Test]
    public async Task FullSync_TwoExportRulesWithScopesThatNeverOverlap_ProvisionsEachIdentityAsync()
    {
        var arrangement = await ArrangeAsync(baseScope: Raj, managersScope: Jane);

        var activity = await RunHrFullSyncAsync(arrangement.Hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap),
                Is.Empty);
            Assert.That(AdPendingExportsFor(arrangement.Ad, MvoIdFor(Jane)).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Create }));
            Assert.That(AdPendingExportsFor(arrangement.Ad, MvoIdFor(Raj)).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Create }));
        }
    }

    [Test]
    public async Task FullSync_DriftOnAnAccountTwoEnforcingRulesCover_CorrectsNothingAndReportsTheOverlapAsync()
    {
        var (directory, cso, description) = await ArrangeEnforcingRulesAsync(staffScope: null, contractorScope: null);

        var activity = await RunHrFullSyncAsync(directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Count(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap),
                Is.EqualTo(1), "drift reports the overlap rather than staging one correction per rule");
            Assert.That(CorrectionsFor(cso, description), Is.Empty);
        }
    }

    [Test]
    public async Task FullSync_DriftOnAnAccountWithEnforcingRulesThatNeverOverlap_EnforcesOnlyTheRuleCoveringItAsync()
    {
        var (directory, cso, description) = await ArrangeEnforcingRulesAsync(staffScope: Jane, contractorScope: Raj);

        var activity = await RunHrFullSyncAsync(directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Where(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.ExportRuleOverlap),
                Is.Empty);
            Assert.That(CorrectionsFor(cso, description), Is.EqualTo(new[] { "Staff" }),
                "only the rule covering Jane enforces its value; the contractor rule does not cover her");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfAnIdentityTwoExportRulesCover_ReportsTheOverlapAndProposesNoExportToItAsync()
    {
        var arrangement = await ArrangeAsync(baseScope: null, managersScope: Jane);
        var janeHrObject = HrObjectFor(arrangement.Hr, Jane);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arrangement.Hr.Id, janeHrObject.Id);

        var overlaps = preview.Errors.Where(e => e.Code == SyncPreviewMessageCode.ExportRuleOverlap).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlaps, Has.Count.EqualTo(1), "the preview reports the overlap the run records, once");
            Assert.That(overlaps.Single().ConnectedSystemId, Is.EqualTo(arrangement.Ad.Id));
            Assert.That(overlaps.Single().Detail, Does.Contain("AD Base Accounts").And.Contain("AD Manager Accounts"));
            Assert.That(preview.Outbound.ProposedExports.Where(pe => pe.ConnectedSystemId == arrangement.Ad.Id), Is.Empty,
                "the run stages nothing for an object two rules cover, so the preview proposes nothing");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfAnIdentityOneOfTwoExportRulesCovers_ProposesItsExportAndNoOverlapAsync()
    {
        var arrangement = await ArrangeAsync(baseScope: Raj, managersScope: Jane);
        var janeHrObject = HrObjectFor(arrangement.Hr, Jane);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(arrangement.Hr.Id, janeHrObject.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Where(e => e.Code == SyncPreviewMessageCode.ExportRuleOverlap), Is.Empty);
            Assert.That(preview.Outbound.ProposedExports.Where(pe => pe.ConnectedSystemId == arrangement.Ad.Id).Select(pe => pe.ChangeType),
                Is.EqualTo(new[] { PendingExportChangeType.Create }));
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfADriftedAccountTwoEnforcingRulesCover_ReportsTheOverlapAndProposesNoCorrectionAsync()
    {
        var (directory, cso, description) = await ArrangeEnforcingRulesAsync(staffScope: null, contractorScope: null);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(directory.Id, cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Count(e => e.Code == SyncPreviewMessageCode.ExportRuleOverlap), Is.EqualTo(1),
                "one error, as the run records one item, however many paths find the overlap");
            Assert.That(ProposedValuesFor(preview, description), Is.Empty, "no correction is proposed for an overlapping object");
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_OfADriftedAccountWithEnforcingRulesThatNeverOverlap_ProposesOnlyTheCoveringRulesCorrectionAsync()
    {
        var (directory, cso, description) = await ArrangeEnforcingRulesAsync(staffScope: Jane, contractorScope: Raj);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(directory.Id, cso.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Errors.Where(e => e.Code == SyncPreviewMessageCode.ExportRuleOverlap), Is.Empty);
            Assert.That(ProposedValuesFor(preview, description), Is.EqualTo(new[] { "Staff" }));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AnotherEnabledExportRuleTargetsTheSameObjectType_WarnsNamingItAsync()
    {
        await ArrangeAsync(baseScope: Raj, managersScope: Jane);
        var baseRule = await SavedRuleAsync("AD Base Accounts");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baseRule.SaveWarnings.Where(w => w.Contains("AD Manager Accounts")).ToList(), Has.Count.EqualTo(1),
                "saving one of two export rules into one Object Type warns once, naming the other");
            Assert.That(baseRule.SaveWarnings.Single(w => w.Contains("AD Manager Accounts")), Does.Contain("user"));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TheOtherExportRuleIsDisabled_DoesNotWarnAsync()
    {
        await ArrangeAsync(baseScope: Raj, managersScope: Jane);
        DbContext.SyncRules.Single(r => r.Name == "AD Manager Accounts").Enabled = false;
        await DbContext.SaveChangesAsync();

        var baseRule = await SavedRuleAsync("AD Base Accounts");

        Assert.That(baseRule.SaveWarnings.Where(w => w.Contains("AD Manager Accounts")), Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TheOtherExportRuleIsForAnotherMetaverseObjectType_DoesNotWarnAsync()
    {
        // People and groups can share one Connected System Object Type: a Metaverse Object has one type, so no object is
        // ever in scope of both.
        var arrangement = await ArrangeAsync(baseScope: Raj, managersScope: Jane);
        DbContext.SyncRules.Remove(DbContext.SyncRules.Single(r => r.Name == "AD Manager Accounts"));
        var groupType = await CreateMvObjectTypeAsync("Group");
        await CreateExportSyncRuleAsync(arrangement.Ad.Id, arrangement.AdType, groupType, "AD Groups");
        await DbContext.SaveChangesAsync();

        var baseRule = await SavedRuleAsync("AD Base Accounts");

        Assert.That(baseRule.SaveWarnings.Where(w => w.Contains("AD Groups")), Is.Empty);
    }

    /// <summary>
    /// Saves the named rule through the one write path the portal, the REST API and PowerShell share, and returns the
    /// instance the save stamped its warnings on.
    /// </summary>
    private async Task<SyncRule> SavedRuleAsync(string name)
    {
        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();

        var rule = await Jim.ConnectedSystems.GetSyncRuleAsync(DbContext.SyncRules.Single(r => r.Name == name).Id)
            ?? throw new InvalidOperationException($"arrange: '{name}' loads");
        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, administrator), Is.True, "the save succeeds: a warning, not a refusal");
        return rule;
    }

    private ConnectedSystemObject HrObjectFor(ConnectedSystem hr, string employeeId) =>
        SyncRepo.ConnectedSystemObjects.Values.Single(c => c.ConnectedSystemId == hr.Id &&
            c.AttributeValues.Any(v => v.Attribute?.Name == "EmployeeId" && v.StringValue == employeeId));

    private static List<string?> ProposedValuesFor(SyncPreviewResult preview, ConnectedSystemObjectTypeAttribute attribute) =>
        preview.Outbound.ProposedExports
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(change => change.AttributeId == attribute.Id)
            .Select(change => change.StringValue)
            .ToList();

    /// <summary>
    /// One directory that is both source and target: its import rule projects Jane (EMP001), and two export rules with
    /// Enforce State on write a constant Description into the same Object Type ("Staff" and "Contractor"), each scoped to
    /// one Employee ID or, when the scope is null, unscoped. Jane's account holds a drifted Description.
    /// </summary>
    private async Task<(ConnectedSystem Directory, ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute Description)> ArrangeEnforcingRulesAsync(
        string? staffScope, string? contractorScope)
    {
        var directory = await CreateConnectedSystemAsync("Directory");
        var externalId = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayName = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var employeeId = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var description = new ConnectedSystemObjectTypeAttribute { Name = "Description", Type = AttributeDataType.Text, Selected = true };
        var userType = await CreateCsoTypeAsync(directory.Id, "user", [externalId, displayName, employeeId, description]);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var import = await CreateImportSyncRuleAsync(directory.Id, userType, mvType, "Directory Import");
        AddImportFlow(import, userType, "DisplayName", mvType.Attributes.First(a => a.Name == "DisplayName"));
        AddImportFlow(import, userType, "EmployeeId", mvEmployeeId);
        await DbContext.SaveChangesAsync();

        await AddEnforcingRuleAsync(directory, userType, mvType, "Directory Staff Policy", "Staff", staffScope, mvEmployeeId, description);
        await AddEnforcingRuleAsync(directory, userType, mvType, "Directory Contractor Policy", "Contractor", contractorScope, mvEmployeeId, description);

        var cso = await CreateCsoAsync(directory.Id, userType, "Jane Smith", Jane);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = description.Id, Attribute = description, StringValue = "Drifted", ConnectedSystemObject = cso
        });
        return (directory, cso, description);
    }

    private async Task AddEnforcingRuleAsync(ConnectedSystem directory, ConnectedSystemObjectType userType, MetaverseObjectType mvType, string name,
        string enforcedDescription, string? scope, MetaverseAttribute mvEmployeeId, ConnectedSystemObjectTypeAttribute description)
    {
        var rule = new SyncRule
        {
            ConnectedSystemId = directory.Id,
            Name = name,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemObjectTypeId = userType.Id,
            ConnectedSystemObjectType = userType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            TargetConnectedSystemAttribute = description,
            TargetConnectedSystemAttributeId = description.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = "\"" + enforcedDescription + "\"" } }
        });
        if (scope != null)
            ScopeToEmployeeId(rule, mvEmployeeId, scope);
        DbContext.SyncRules.Add(rule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(rule);
    }

    private List<string?> CorrectionsFor(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute description) =>
        SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == cso.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(change => change.AttributeId == description.Id)
            .Select(change => change.StringValue)
            .ToList();

    private sealed record Arrangement(ConnectedSystem Hr, ConnectedSystem Ad, ConnectedSystemObjectType AdType);

    /// <summary>
    /// HR projects Jane (EMP001) and Raj (EMP002). Two export rules provision into one AD Object Type, each writing a
    /// different attribute: "AD Base Accounts" and "AD Manager Accounts", each scoped to one Employee ID or, when the
    /// scope is null, unscoped.
    /// </summary>
    private async Task<Arrangement> ArrangeAsync(string? baseScope, string? managersScope)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User");
        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        AddImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        await CreateCsoAsync(hr.Id, hrType, "Jane Smith", Jane);
        await CreateCsoAsync(hr.Id, hrType, "Raj Patel", Raj);

        var ad = await CreateConnectedSystemAsync("AD");
        var adType = await CreateCsoTypeAsync(ad.Id, "user");
        var baseRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Base Accounts");
        AddExportFlow(baseRule, adType, "DisplayName", mvDisplayName);
        var managersRule = await CreateExportSyncRuleAsync(ad.Id, adType, mvType, "AD Manager Accounts");
        AddExportFlow(managersRule, adType, "EmployeeId", mvEmployeeId);
        if (baseScope != null)
            ScopeToEmployeeId(baseRule, mvEmployeeId, baseScope);
        if (managersScope != null)
            ScopeToEmployeeId(managersRule, mvEmployeeId, managersScope);
        await DbContext.SaveChangesAsync();

        return new Arrangement(hr, ad, adType);
    }

    /// <summary>
    /// Stands an exported account up for the identity: the Connected System Object the Bootstrap run would have
    /// provisioned, confirmed, with its Create already sent. With overlapping rules the Bootstrap run provisions
    /// nothing, so the account is joined by hand, as one an earlier configuration provisioned.
    /// </summary>
    private async Task<ConnectedSystemObject> JoinedAccountAsync(Arrangement arrangement, Guid mvoId)
    {
        var account = await CreateCsoAsync(arrangement.Ad.Id, arrangement.AdType, "Jane Smith", Jane);
        account.MetaverseObjectId = mvoId;
        account.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        account.Status = ConnectedSystemObjectStatus.Normal;
        account.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(account);
        return account;
    }

    private Guid MvoIdFor(string employeeId) =>
        SyncRepo.MetaverseObjects.Values.Single(m => m.AttributeValues.Any(v => v.Attribute?.Name == "EmployeeId" && v.StringValue == employeeId)).Id;

    private List<PendingExport> AdPendingExportsFor(ConnectedSystem ad, Guid mvoId) =>
        SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == ad.Id && pe.SourceMetaverseObjectId == mvoId)
            .ToList();

    private static void ScopeToEmployeeId(SyncRule rule, MetaverseAttribute employeeId, string value) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    MetaverseAttribute = employeeId,
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

    private async Task<Activity> RunHrFullSyncAsync(ConnectedSystem hr, string profileName = "Full Sync")
    {
        var profile = await CreateRunProfileAsync(hr.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        hr = await ReloadEntityAsync(hr);
        var activity = await CreateActivityAsync(hr.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, hr, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
