// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2091 through a real Full Synchronisation: an export Attribute Flow that targets <c>password</c>, configured before
/// the attribute was denied, stages nothing for it. The execution item records why, naming the attribute and never the
/// value, and the object's other attributes still export.
/// </summary>
[TestFixture]
public class CredentialAttributeExportSynchronisationTests : WorkflowTestBase
{
    private const string Secret = "S3cret!Value";

    [Test]
    public async Task FullSync_ExportFlowTargetingPassword_StagesNothingForItAndRecordsWhyAsync()
    {
        var hr = await CreateConnectedSystemAsync("HR");
        var hrSecret = new ConnectedSystemObjectTypeAttribute { Name = "InitialSecret", Type = AttributeDataType.Text, Selected = true };
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            hrSecret
        ]);

        var target = await CreateConnectedSystemAsync("SCIM");
        var targetPassword = new ConnectedSystemObjectTypeAttribute { Name = "password", Type = AttributeDataType.Text, Selected = true };
        var targetEmployeeId = new ConnectedSystemObjectTypeAttribute { Name = "employeeNumber", Type = AttributeDataType.Text, Selected = true };
        var targetType = await CreateCsoTypeAsync(target.Id, "User",
        [
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            targetPassword,
            targetEmployeeId
        ]);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvSecret = new MetaverseAttribute
        {
            Name = "Initial Secret", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType], PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvSecret);
        await DbContext.SaveChangesAsync();
        if (!mvType.Attributes.Contains(mvSecret))
            mvType.Attributes.Add(mvSecret);
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var importRule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, "HR Import");
        Map(importRule, mvEmployeeId, hrType.Attributes.Single(a => a.Name == "EmployeeId"));
        Map(importRule, mvSecret, hrSecret);

        var exportRule = await CreateExportSyncRuleAsync(target.Id, targetType, mvType, "SCIM Export");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, TargetConnectedSystemAttribute = targetPassword, TargetConnectedSystemAttributeId = targetPassword.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvSecret, MetaverseAttributeId = mvSecret.Id } }
        });
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, TargetConnectedSystemAttribute = targetEmployeeId, TargetConnectedSystemAttributeId = targetEmployeeId.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvEmployeeId, MetaverseAttributeId = mvEmployeeId.Id } }
        });
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, "Priya Shah", "EMP001");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = hrSecret.Id, Attribute = hrSecret, StringValue = Secret, ConnectedSystemObject = cso
        });

        var activity = await RunFullSyncAsync(hr);

        var changes = SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObject?.ConnectedSystemId == target.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .ToList();
        var error = Errors(activity).SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.AttributeId), Does.Not.Contain(targetPassword.Id), "nothing is staged for password");
            Assert.That(changes.Select(c => c.StringValue), Does.Not.Contain(Secret));
            Assert.That(changes.Where(c => c.AttributeId == targetEmployeeId.Id).Select(c => c.StringValue), Is.EqualTo(new[] { "EMP001" }),
                "the object's other attributes still export");
            Assert.That(error?.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.CredentialAttributeNotExported));
            Assert.That(error?.ErrorMessage, Does.Contain("'password'").And.Contain("Password Synchronisation").And.Not.Contain(Secret));
        }
    }

    private static void Map(SyncRule importRule, MetaverseAttribute target, ConnectedSystemObjectTypeAttribute source) =>
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });

    private List<ActivityRunProfileExecutionItem> Errors(Activity activity) => SyncRepo.Rpeis.Values
        .Where(r => r.ActivityId == activity.Id)
        .Concat(activity.RunProfileExecutionItems)
        .DistinctBy(r => r.Id)
        .Where(r => r.ErrorType is not (null or ActivityRunProfileExecutionItemErrorType.NotSet))
        .ToList();

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem)
    {
        var profile = await CreateRunProfileAsync(connectedSystem.Id, $"Full Sync {Guid.NewGuid():N}", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(connectedSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, connectedSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }
}
