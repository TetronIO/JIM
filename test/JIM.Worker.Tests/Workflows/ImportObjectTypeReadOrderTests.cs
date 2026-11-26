// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2521 decision 7: a Full Import hands a Connector that reads its Object Types one search at a time
/// (<see cref="IConnectorObjectTypeReadOrder"/>) the order a synchronisation pages them in (#2492), referenced types
/// first, once, before the connection opens, so the objects a reference names have usually been read before it.
/// </summary>
[TestFixture]
public class ImportObjectTypeReadOrderTests : WorkflowTestBase
{
    private ConnectedSystem _system = null!;
    private ConnectedSystemObjectType _group = null!;
    private ConnectedSystemObjectType _person = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        // The group type is made first, so it has the lower id, and an order by id alone would read it first.
        _system = await CreateConnectedSystemAsync("Directory");
        _group = await CreateCsoTypeAsync(_system.Id, "group");
        _person = await CreateCsoTypeAsync(_system.Id, "person");
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true,
            ReferencedObjectTypeId = _person.Id,
            ConnectedSystemObjectType = _group
        };
        _group.Attributes.Add(member);
        await DbContext.SaveChangesAsync();

        // An import rule flowing the group's member attribute into a Metaverse reference attribute: what makes the group
        // type depend on the person type.
        var mvGroup = await CreateMvObjectTypeAsync("Group");
        var members = new MetaverseAttribute { Name = "Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var rule = await CreateImportSyncRuleAsync(_system.Id, _group, mvGroup, "Import groups");
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = members };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = member, ConnectedSystemAttributeId = member.Id });
        rule.AttributeFlowRules.Add(mapping);
    }

    [Test]
    public async Task FullImport_ConnectorReadsObjectTypesInOrder_HandsItTheDependencyOrderBeforeOpeningTheConnectionAsync()
    {
        var connector = new ReadOrderTestConnector();

        await (await PrepareAsync(connector, ConnectedSystemRunType.FullImport)).PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.OrdersSet, Has.Count.EqualTo(1), "the order is handed over once per run");
            Assert.That(connector.OrdersSet.Single().Select(group => group.ToList()), Is.EqualTo(new[] { new[] { _person.Id }, new[] { _group.Id } }),
                "the person type, which group members point at, is read first");
            Assert.That(connector.Calls.Take(2), Is.EqualTo(new[] { "SetOrder", "Open" }));
        }
    }

    [Test]
    public async Task FullImport_UndeclaredReferenceWithNoValuesHeldYet_ReadsTheTypesTogetherAsync()
    {
        // LDAP's member declares no target, and on a Connected System's first import JIM holds no values to see what it
        // points at. Taking that as "points at nothing" would order the types by id alone (groups first here, the wrong
        // way round); it is taken as "may point at anything", so the types are read together, as before.
        var manager = new ConnectedSystemObjectTypeAttribute
        {
            Name = "manager",
            Type = AttributeDataType.Reference,
            Selected = true,
            ConnectedSystemObjectType = _person
        };
        _person.Attributes.Add(manager);
        var undeclaredMember = _group.Attributes.Single(a => a.Name == "member");
        undeclaredMember.ReferencedObjectTypeId = null;
        await DbContext.SaveChangesAsync();

        var mvPerson = await CreateMvObjectTypeAsync("Person");
        var managerMv = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference };
        var rule = await CreateImportSyncRuleAsync(_system.Id, _person, mvPerson, "Import people");
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = managerMv };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = manager, ConnectedSystemAttributeId = manager.Id });
        rule.AttributeFlowRules.Add(mapping);
        var connector = new ReadOrderTestConnector();

        await (await PrepareAsync(connector, ConnectedSystemRunType.FullImport)).PerformImportAsync();

        Assert.That(connector.OrdersSet.Single().Select(group => group.ToList()), Is.EqualTo(new[] { new[] { _group.Id, _person.Id } }));
    }

    [Test]
    public async Task DeltaImport_ConnectorReadsObjectTypesInOrder_HandsItNoOrderAsync()
    {
        var connector = new ReadOrderTestConnector();

        await (await PrepareAsync(connector, ConnectedSystemRunType.DeltaImport)).PerformImportAsync();

        Assert.That(connector.OrdersSet, Is.Empty, "a Delta Import reads changes, which come in the order they were made");
    }

    private async Task<SyncImportTaskProcessor> PrepareAsync(IConnector connector, ConnectedSystemRunType runType)
    {
        var runProfile = await CreateRunProfileAsync(_system.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(_system.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(_system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        return new SyncImportTaskProcessor(Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(), connector, _system, runProfile, workerTask, new CancellationTokenSource());
    }

    private sealed class ReadOrderTestConnector : IConnector, IConnectorImportUsingCalls, IConnectorObjectTypeReadOrder
    {
        public string Identifier => "test.connector";
        public string Name => "MockConnector";
        public string? Description => null;
        public string? Url => null;

        public List<IReadOnlyList<IReadOnlyList<int>>> OrdersSet { get; } = [];

        public List<string> Calls { get; } = [];

        public void SetObjectTypeReadOrder(IReadOnlyList<IReadOnlyList<int>> groups)
        {
            Calls.Add("SetOrder");
            OrdersSet.Add(groups);
        }

        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger) => Calls.Add("Open");

        public string? CloseImportConnection()
        {
            Calls.Add("Close");
            return null;
        }

        public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens,
            string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken, IConnectorProgress progress)
        {
            Calls.Add("Import");
            return Task.FromResult(new ConnectedSystemImportResult());
        }
    }
}
