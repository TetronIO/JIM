// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Preview fidelity for a target account holding a change until its Initial Password is delivered (#2039). Active
/// Directory was given the account disabled (514) and the held change asserts the enabled value (512), which the
/// password's delivery releases. Expression mappings are re-evaluated on any change to the person, so every HR change
/// during the hold re-states 512; the run drops that restatement, stages nothing and records no Pending Export. The
/// preview must say the same, or an administrator reads an export the run will not make.
/// Topology: an "HR" source projecting a Person (EmployeeId, DisplayName, and Title flowing to the Metaverse's Type
/// attribute), and a "Directory" target whose export rule flows DisplayName and sets userAccountControl to 512.
/// </summary>
public partial class SyncPreviewFidelityTests
{
    private const int EnabledAccount = 512;
    private const int DisabledAccount = 514;

    /// <summary>
    /// The account exists and is joined; the held change sits on an Update Pending Export.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ChangeOnlyRestatingAChangeHeldForAnInitialPassword_ProposesNoExportTheRunDoesNotStageAsync()
    {
        var ctx = await SetUpHeldChangeAsync(onAnUnconfirmedCreate: false);
        SetWithdrawalValue(ctx.HrCso, "Title", "Director");
        await ModifyCsoAsync(ctx.HrCso);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        AssertNoExportProposedAndNoneStaged(ctx, preview, activity);
    }

    /// <summary>
    /// The account was provisioned and its Create sent, but no import has confirmed it yet, so it is still Pending
    /// Provisioning and the held change sits on the Create itself. The run appends a new change onto that Create rather
    /// than replacing it, and a restatement of the held change is not appended at all.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ChangeOnlyRestatingAChangeHeldOnAnUnconfirmedCreate_ProposesNoExportTheRunDoesNotStageAsync()
    {
        var ctx = await SetUpHeldChangeAsync(onAnUnconfirmedCreate: true);
        SetWithdrawalValue(ctx.HrCso, "Title", "Director");
        await ModifyCsoAsync(ctx.HrCso);
        var queuedChangeIds = SyncRepo.PendingExports[ctx.HeldPendingExportId].AttributeValueChanges.Select(c => c.Id).ToList();

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        Assert.That(SyncRepo.PendingExports[ctx.HeldPendingExportId].AttributeValueChanges.Select(c => c.Id), Is.EquivalentTo(queuedChangeIds),
            "arrange: the run appends nothing onto the unconfirmed Create");
        AssertNoExportProposedAndNoneStaged(ctx, preview, activity);
    }

    /// <summary>
    /// A DisplayName change staged during the hold is reverted to the value the account already holds. The restated
    /// enabled value is dropped and the reverted change withdrawn; the Pending Export holding the change is left in place
    /// with only the held change on it, so the withdrawal is all the run records and all the preview proposes. Rebuilding
    /// that export instead would record a Pending Export the preview does not show, re-stamped with this synchronisation's
    /// item.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ChangeStagedDuringTheHoldRevertedToTheAccountsValue_ProposesOnlyTheWithdrawalTheRunMakesAsync()
    {
        var ctx = await SetUpHeldChangeAsync(onAnUnconfirmedCreate: false);
        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alicia");
        await ModifyCsoAsync(ctx.HrCso);
        await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));
        var holding = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemObjectId == ctx.DirectoryCso.Id);
        var heldChange = holding.AttributeValueChanges.Single(c => c.AwaitsInitialPassword);
        Assert.That(holding.AttributeValueChanges.Where(c => !c.AwaitsInitialPassword).Select(c => c.StringValue), Is.EqualTo(new[] { "Alicia" }),
            "arrange: the change staged during the hold is queued beside the held change");
        var queuingItemId = holding.QueuedByRunProfileExecutionItemId;

        SetWithdrawalValue(ctx.HrCso, "DisplayName", "Alice");
        await ModifyCsoAsync(ctx.HrCso);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.HrCso.Id);
        var activity = await RunFullSyncAsync(await ReloadEntityAsync(ctx.Hr));

        var described = DescribeTree(preview.OutcomeTree);
        var queuedForAccount = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == ctx.DirectoryCso.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn)),
                "the preview proposes the withdrawal");
            Assert.That(preview.Outbound.ProposedExports, Is.Empty, "the preview proposes no export");
            Assert.That(described, Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
            Assert.That(queuedForAccount.Select(pe => pe.Id), Is.EqualTo(new[] { holding.Id }),
                "the Pending Export holding the change is kept, not rebuilt");
            Assert.That(queuedForAccount.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Id), Is.EqualTo(new[] { heldChange.Id }),
                "only the held change remains queued");
            Assert.That(queuedForAccount.Single().QueuedByRunProfileExecutionItemId, Is.EqualTo(queuingItemId),
                "the item that queued it still names its cause");
        }
    }

    private void AssertNoExportProposedAndNoneStaged(HeldChangeContext ctx, SyncPreviewResult preview, Activity activity)
    {
        var described = DescribeTree(preview.OutcomeTree);
        var queuedForAccount = SyncRepo.PendingExports.Values.Where(pe => pe.ConnectedSystemObjectId == ctx.DirectoryCso.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedForAccount.Select(pe => pe.Id), Is.EqualTo(new[] { ctx.HeldPendingExportId }),
                "arrange: the run stages nothing for the account and leaves the Pending Export holding the change in place");
            Assert.That(preview.Outbound.ProposedExports, Is.Empty, "the preview proposes no export the run does not stage");
            Assert.That(described, Does.Not.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)));
            Assert.That(described, Is.EqualTo(DescribeTree(MapRealOutcomeTree(activity))));
        }
    }

    // ---- Topology ----

    private sealed record HeldChangeContext(
        ConnectedSystem Hr,
        ConnectedSystemObject HrCso,
        ConnectedSystemObject DirectoryCso,
        Guid HeldPendingExportId);

    /// <summary>
    /// Synchronises Alice into a Directory account whose Pending Export holds the enabled value until her Initial
    /// Password is delivered: sent, awaiting its confirming import, and marked as held. Either the account exists,
    /// joined and holding the interim disabled value, and the held change is an Update; or the account was provisioned
    /// and the held change is on its Create, sent but not yet confirmed.
    /// </summary>
    private async Task<HeldChangeContext> SetUpHeldChangeAsync(bool onAnUnconfirmedCreate)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvTitle = mvType.Attributes.First(a => a.Name == "Type");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "User", WithdrawalAttributes());
        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "user",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "userAccountControl", Type = AttributeDataType.Number, Selected = true }
        ]);
        var userAccountControl = directoryType.Attributes.Single(a => a.Name == "userAccountControl");

        var hrImport = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import", enableProjection: true);
        AddWithdrawalImportFlow(hrImport, hrType, "EmployeeId", mvEmployeeId);
        AddWithdrawalImportFlow(hrImport, hrType, "DisplayName", mvDisplayName);
        AddWithdrawalImportFlow(hrImport, hrType, "Title", mvTitle);

        var directoryImport = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import", enableProjection: false);
        AddWithdrawalImportFlow(directoryImport, directoryType, "EmployeeId", mvEmployeeId);
        var employeeIdSource = directoryType.Attributes.Single(a => a.Name == "EmployeeId");
        directoryImport.ObjectMatchingRules.Add(new ObjectMatchingRule
        {
            SyncRule = directoryImport,
            SyncRuleId = directoryImport.Id,
            Order = 0,
            CaseSensitive = true,
            TargetMetaverseAttribute = mvEmployeeId,
            TargetMetaverseAttributeId = mvEmployeeId.Id,
            Sources = { new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = employeeIdSource, ConnectedSystemAttributeId = employeeIdSource.Id } }
        });

        var directoryExport = await CreateExportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Export", enableProvisioning: onAnUnconfirmedCreate);
        AddWithdrawalExportFlow(directoryExport, directoryType, mvDisplayName, "DisplayName");
        directoryExport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = directoryExport,
            SyncRuleId = directoryExport.Id,
            TargetConnectedSystemAttribute = userAccountControl,
            TargetConnectedSystemAttributeId = userAccountControl.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, Expression = EnabledAccount.ToString() } }
        });
        await DbContext.SaveChangesAsync();

        var hrCso = SeedWithdrawalCso(hr, hrType, "E1", "Alice", "Engineer");

        if (onAnUnconfirmedCreate)
        {
            await RunFullSyncAsync(await ReloadEntityAsync(hr));
            var create = SyncRepo.PendingExports.Values.Single(pe => pe.ConnectedSystemId == directory.Id);
            Assert.That(create.ChangeType, Is.EqualTo(PendingExportChangeType.Create), "arrange: the account is provisioned");
            var provisioned = SyncRepo.ConnectedSystemObjects[create.ConnectedSystemObjectId!.Value];
            Assert.That(provisioned.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning), "arrange: no import has confirmed it");

            // The Create was sent: the account was written disabled and the enabled value is held for the password.
            create.Status = PendingExportStatus.Exported;
            create.LastAttemptedAt = DateTime.UtcNow;
            foreach (var change in create.AttributeValueChanges)
            {
                change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
                change.ExportAttemptCount = 1;
                change.AwaitsInitialPassword = change.AttributeId == userAccountControl.Id;
            }
            Assert.That(create.AttributeValueChanges.Count(c => c.AwaitsInitialPassword && c.IntValue == EnabledAccount), Is.EqualTo(1),
                "arrange: the Create holds the enabled value");

            return new HeldChangeContext(hr, hrCso, provisioned, create.Id);
        }

        var directoryCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = directory.Id,
            TypeId = directoryType.Id,
            Type = directoryType,
            ConnectedSystem = SyncRepo.ConnectedSystems[directory.Id],
            Created = DateTime.UtcNow
        };
        directoryCso.AttributeValues.AddRange(new (string Name, string? StringValue, Guid? GuidValue, int? IntValue)[]
        {
            ("ExternalId", null, Guid.NewGuid(), null), ("EmployeeId", "E1", null, null), ("DisplayName", "Alice", null, null),
            ("userAccountControl", null, null, DisabledAccount)
        }.Select(value =>
        {
            var attribute = directoryType.Attributes.Single(a => a.Name == value.Name);
            return new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), ConnectedSystemObject = directoryCso, AttributeId = attribute.Id, Attribute = attribute,
                StringValue = value.StringValue, GuidValue = value.GuidValue, IntValue = value.IntValue
            };
        }));
        SyncRepo.SeedConnectedSystemObject(directoryCso);

        await RunFullSyncAsync(await ReloadEntityAsync(hr));
        await RunFullSyncAsync(await ReloadEntityAsync(directory));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReloadEntityAsync(directoryCso)).MetaverseObjectId, Is.Not.Null, "arrange: the Directory account is joined");
            Assert.That(directoryCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "arrange: the Directory account exists in the directory");
        }

        SyncRepo.ClearAllPendingExports();
        var heldUpdate = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = directory.Id,
            ConnectedSystemObjectId = directoryCso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            CreatedAt = DateTime.UtcNow,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = userAccountControl.Id,
                    Attribute = userAccountControl,
                    ChangeType = PendingExportAttributeChangeType.Update,
                    IntValue = EnabledAccount,
                    Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
                    ExportAttemptCount = 1,
                    AwaitsInitialPassword = true
                }
            ]
        };
        SyncRepo.SeedPendingExport(heldUpdate);

        return new HeldChangeContext(hr, hrCso, directoryCso, heldUpdate.Id);
    }
}
