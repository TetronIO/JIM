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
/// An ambiguous match records its candidates on the Activity item (#2052). Before this, the item carried only a list
/// of Metaverse Object ids inside its error message, so working out which identities collided meant copying ids into
/// a search one at a time.
/// </summary>
[TestFixture]
public class ObjectMatchingAmbiguityWorkflowTests : WorkflowTestBase
{
    [Test]
    public async Task FullSync_ObjectMatchesTwoIdentities_RecordsBothAsCandidatesOnTheActivityItemAsync()
    {
        var (hr, hrCso, matchingRule, identities) = await ArrangeHrObjectMatchingTwoIdentitiesAsync();

        var activity = await RunFullSyncAsync(hr);

        hrCso = await ReloadEntityAsync(hrCso);
        var item = SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .Single(i => i.ConnectedSystemObjectId == hrCso.Id);
        var candidates = SyncRepo.MatchCandidates.Where(c => c.ActivityRunProfileExecutionItemId == item.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrCso.MetaverseObjectId, Is.Null);
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.AmbiguousMatch));
            Assert.That(candidates.Select(c => c.CandidateId), Is.EquivalentTo(identities));
            Assert.That(candidates.All(c => c.CandidateTotal == 2), Is.True);
            Assert.That(candidates.All(c => c.ObjectMatchingRuleId == matchingRule.Id), Is.True);
            Assert.That(candidates.All(c => c.MatchedValue == "EMP001"), Is.True);
        }
    }

    [Test]
    public async Task PreviewSyncForCsoAsync_ObjectMatchesTwoIdentities_CarriesBothCandidatesOnTheErrorAsync()
    {
        var (hr, hrCso, matchingRule, identities) = await ArrangeHrObjectMatchingTwoIdentitiesAsync();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(hr.Id, hrCso.Id);

        var error = preview.Errors.Single(e => e.Code == SyncPreviewMessageCode.AmbiguousMatch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.MatchCandidates.Select(c => c.CandidateId), Is.EquivalentTo(identities));
            Assert.That(error.MatchCandidates.All(c => c.ObjectMatchingRuleId == matchingRule.Id), Is.True);
            Assert.That(error.MatchCandidates.All(c => c.CandidateTotal == 2), Is.True);
        }
    }

    /// <summary>
    /// The Directory holds the same Employee ID on two accounts and projects both; HR's object, matching on Employee
    /// ID with no projection of its own, then finds two identities.
    /// </summary>
    private async Task<(ConnectedSystem Hr, ConnectedSystemObject HrCso, ObjectMatchingRule MatchingRule, List<Guid> Identities)>
        ArrangeHrObjectMatchingTwoIdentitiesAsync()
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryImportRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        var directoryEmployeeIdAttr = directoryType.Attributes.First(a => a.Name == "EmployeeId");
        directoryImportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryImportRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = directoryEmployeeIdAttr, ConnectedSystemAttributeId = directoryEmployeeIdAttr.Id } }
        });

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var hrImportRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: false);
        var matchingRule = new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            SyncRule = hrImportRule,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = hrEmployeeIdAttr, ConnectedSystemAttributeId = hrEmployeeIdAttr.Id }]
        };
        hrImportRule.ObjectMatchingRules.Add(matchingRule);
        await DbContext.SaveChangesAsync();

        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith", "EMP001");
        await CreateCsoAsync(directory.Id, directoryType, "Jane Smith (admin)", "EMP001");
        var hrCso = await CreateCsoAsync(hr.Id, hrType, "Jane Smith (HR)", "EMP001");
        await RunFullSyncAsync(directory);
        var identities = SyncRepo.MetaverseObjects.Keys.ToList();
        Assert.That(identities, Has.Count.EqualTo(2), "precondition: the Directory projects both accounts");
        return (hr, hrCso, matchingRule, identities);
    }

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
