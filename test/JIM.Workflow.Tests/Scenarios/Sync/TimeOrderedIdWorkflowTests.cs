// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Workflow.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Workflow.Tests.Scenarios.Sync;

/// <summary>
/// Every row a provisioning cycle writes in bulk has a time-ordered (version 7) id (#2929): the Connected System Objects
/// an import creates and their values, the Metaverse Objects a synchronisation projects and their values, the Pending
/// Exports it stages with their changes, the Connected System Objects it provisions with their DN, the values an export
/// applies, and the values a later import and synchronisation add.
/// </summary>
/// <remarks>
/// Ids are collected after every step, since a step can delete what an earlier one wrote (a confirmed Pending Export).
/// The ids are checked wherever they came from, so this covers both the code that builds a row with an id of its own and
/// the writer that gives one to a row that has none.
/// </remarks>
[TestFixture]
public class TimeOrderedIdWorkflowTests
{
    private const int UserCount = 5;

    private WorkflowTestHarness _harness = null!;
    private readonly Dictionary<Guid, (string Kind, string Step)> _ids = [];

    [SetUp]
    public void SetUp()
    {
        _harness = new WorkflowTestHarness();
        _ids.Clear();
    }

    [TearDown]
    public void TearDown() => _harness?.Dispose();

    [Test]
    public async Task ProvisioningCycle_EveryStep_WritesVersion7IdsAsync()
    {
        await SetUpProvisioningScenarioAsync();
        var users = Enumerable.Range(0, UserCount).Select(i => (EmployeeId: Guid.NewGuid(), Name: $"User {i}")).ToList();

        _harness.GetConnector("HR").QueueImportObjects(users.Select(u => SourceUser(u.EmployeeId, u.Name, "Sales")));
        await _harness.ExecuteFullImportAsync("HR");
        Collect("after the Full Import");

        await _harness.ExecuteFullSyncAsync("HR");
        Collect("after the Full Synchronisation");

        await _harness.ExecuteExportAsync("AD");
        Collect("after the export");

        var ad = _harness.GetConnector("AD");
        ad.WithConfirmingImportFactory(ConfirmingObject);
        ad.QueueConfirmingImport();
        await _harness.ExecuteConfirmingImportAsync("AD");
        Collect("after the confirming import");

        // A mover: every user's department and display name change, so the import updates the objects it holds, the
        // synchronisation updates the Metaverse Objects, and an update is staged for each account.
        _harness.GetConnector("HR").QueueImportObjects(users.Select(u => SourceUser(u.EmployeeId, u.Name + " (moved)", "Finance")));
        await _harness.ExecuteFullImportAsync("HR");
        Collect("after the second Full Import");

        await _harness.ExecuteFullSyncAsync("HR");
        Collect("after the second Full Synchronisation");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_ids.Values.Select(id => id.Kind).Distinct(), Is.SupersetOf(new[]
            {
                "Connected System Object", "Connected System Object value", "Metaverse Object", "Metaverse Object value",
                "Pending Export", "Pending Export change"
            }), "the cycle wrote every kind of row");
            var random = _ids.Where(id => id.Key.Version != 7).Select(id => $"{id.Value.Kind} {id.Value.Step}: {id.Key} (version {id.Key.Version})").ToList();
            Assert.That(random, Is.Empty, "every id is time-ordered (version 7)");
        }
    }

    private void Collect(string step)
    {
        var repository = _harness.SyncRepo;
        foreach (var cso in repository.ConnectedSystemObjects.Values)
        {
            Record(cso.Id, "Connected System Object", step);
            foreach (var value in cso.AttributeValues)
                Record(value.Id, "Connected System Object value", step);
        }

        foreach (var mvo in repository.MetaverseObjects.Values)
        {
            Record(mvo.Id, "Metaverse Object", step);
            foreach (var value in mvo.AttributeValues)
                Record(value.Id, "Metaverse Object value", step);
        }

        foreach (var pendingExport in repository.PendingExports.Values)
        {
            Record(pendingExport.Id, "Pending Export", step);
            foreach (var change in pendingExport.AttributeValueChanges)
                Record(change.Id, "Pending Export change", step);
        }

        foreach (var item in repository.Rpeis.Values)
            Record(item.Id, "Run Profile Execution Item", step);
        foreach (var edge in repository.CausalEdges)
            Record(edge.Id, "Causal Edge", step);
    }

    private void Record(Guid id, string kind, string step) => _ids.TryAdd(id, (kind, step));

    private async Task SetUpProvisioningScenarioAsync()
    {
        await _harness.CreateConnectedSystemAsync("HR");
        await _harness.CreateObjectTypeAsync("HR", "User", t => t
            .WithGuidExternalId("employeeId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("department"));

        await _harness.CreateConnectedSystemAsync("AD");
        await _harness.CreateObjectTypeAsync("AD", "User", t => t
            .WithGuidExternalId("objectGUID")
            .WithStringSecondaryExternalId("distinguishedName")
            .WithStringAttribute("cn")
            .WithStringAttribute("department"));

        var personType = await _harness.CreateMetaverseObjectTypeAsync("Person", t => t
            .WithGuidAttribute("employeeId")
            .WithStringAttribute("displayName")
            .WithStringAttribute("department"));

        var hrUserType = _harness.GetObjectType("HR", "User");
        var adUserType = _harness.GetObjectType("AD", "User");
        var mvDisplayName = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "displayName");
        var mvDepartment = await _harness.DbContext.MetaverseAttributes.FirstAsync(a => a.Name == "department");

        await _harness.CreateSyncRuleAsync("HR Import", "HR", "User", personType, SyncRuleDirection.Import, r => r
            .WithProjection()
            .WithAttributeFlow(mvDisplayName, hrUserType.Attributes.First(a => a.Name == "displayName"))
            .WithAttributeFlow(mvDepartment, hrUserType.Attributes.First(a => a.Name == "department")));

        await _harness.CreateSyncRuleAsync("AD Export", "AD", "User", personType, SyncRuleDirection.Export, r => r
            .WithProvisioning()
            .WithAttributeFlow(mvDisplayName, adUserType.Attributes.First(a => a.Name == "cn"))
            .WithAttributeFlow(mvDepartment, adUserType.Attributes.First(a => a.Name == "department"))
            .WithExpressionFlow("\"CN=\" + mv[\"displayName\"] + \",OU=Users,DC=panoply,DC=local\"",
                adUserType.Attributes.First(a => a.Name == "distinguishedName")));
    }

    private static ConnectedSystemImportObject SourceUser(Guid employeeId, string displayName, string department) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "User",
        Attributes =
        [
            new() { Name = "employeeId", GuidValues = [employeeId] },
            new() { Name = "displayName", StringValues = [displayName] },
            new() { Name = "department", StringValues = [department] }
        ]
    };

    private static ConnectedSystemImportObject ConfirmingObject(PendingExport pendingExport) => new()
    {
        ChangeType = ObjectChangeType.NotSet,
        ObjectType = "User",
        Attributes =
        [
            new() { Name = "objectGUID", GuidValues = [Guid.NewGuid()] },
            new() { Name = "distinguishedName", StringValues = [Value(pendingExport, "distinguishedName")] },
            new() { Name = "cn", StringValues = [Value(pendingExport, "cn")] },
            new() { Name = "department", StringValues = [Value(pendingExport, "department")] }
        ]
    };

    private static string Value(PendingExport pendingExport, string attributeName) =>
        pendingExport.AttributeValueChanges.First(c => c.Attribute?.Name == attributeName).StringValue!;
}
