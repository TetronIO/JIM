// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Web;
using JIM.Connectors.Graph;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// What the Microsoft Graph Connector's import tests share: a fake tenant, a Connected System as schema import leaves it,
/// and a run that pages through an import as JIM does.
/// </summary>
public abstract class GraphImportTestBase
{
    protected ILogger Logger { get; private set; } = null!;

    private protected FakeGraphTenant Tenant { get; private set; } = null!;

    [SetUp]
    public void SetUpTenant()
    {
        Logger = new LoggerConfiguration().CreateLogger();
        Tenant = new FakeGraphTenant();
    }

    [TearDown]
    public void TearDownLogger()
    {
        (Logger as IDisposable)?.Dispose();
    }

    protected async Task<List<ConnectedSystemImportObject>> ImportAsync(params ConnectedSystemObjectType[] objectTypes) =>
        (await RunAsync(ConnectedSystem(objectTypes))).SelectMany(r => r.ImportObjects).ToList();

    protected Task<List<ConnectedSystemImportResult>> RunAsync(ConnectedSystem connectedSystem, int pageSize = 100) =>
        RunAsync(connectedSystem, ConnectedSystemRunType.FullImport, null, pageSize);

    /// <summary>Pages through an import as JIM does, replaying the pagination tokens until none come back.</summary>
    protected async Task<List<ConnectedSystemImportResult>> RunAsync(ConnectedSystem connectedSystem, ConnectedSystemRunType runType,
        string? persistedConnectorData, int pageSize = 100)
    {
        var connector = new StubbedTransportGraphConnector(Tenant.Handler);
        var runProfile = new ConnectedSystemRunProfile { Name = runType.ToString(), RunType = runType, PageSize = pageSize };
        connector.OpenImportConnection(connectedSystem.SettingValues, persistedConnectorData, Logger);
        var results = new List<ConnectedSystemImportResult>();
        var tokens = new List<ConnectedSystemPaginationToken>();
        try
        {
            for (var page = 0; page < 50; page++)
            {
                var result = await connector.ImportAsync(connectedSystem, runProfile, tokens, persistedConnectorData, Logger, CancellationToken.None, new RecordingConnectorProgress());
                results.Add(result);
                if (result.PaginationTokens.Count == 0)
                    return results;
                tokens = result.PaginationTokens;
            }
        }
        finally
        {
            connector.CloseImportConnection();
        }

        Assert.Fail("The import did not finish within 50 pages.");
        return results;
    }

    protected static ConnectedSystem ConnectedSystem(params ConnectedSystemObjectType[] objectTypes) => new()
    {
        Name = "Entra ID",
        ObjectTypes = objectTypes.ToList(),
        SettingValues =
        [
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTenantId }, StringValue = "7a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientId }, StringValue = "1c2d3e4f-5a6b-4c7d-8e9f-a0b1c2d3e4f5" },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingAuthenticationMethod }, StringValue = GraphConnectorConstants.AuthMethodClientSecret },
            new() { Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingClientSecret }, StringEncryptedValue = "client-secret-value" }
        ]
    };

    /// <summary>The Connected System with Track Source of Authority Changes turned on.</summary>
    protected static ConnectedSystem Tracked(ConnectedSystem connectedSystem)
    {
        connectedSystem.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Setting = new ConnectorDefinitionSetting { Name = GraphConnectorConstants.SettingTrackSourceOfAuthority, Type = ConnectedSystemSettingType.CheckBox },
            CheckboxValue = true
        });
        return connectedSystem;
    }

    protected static ConnectedSystemObjectType Users(params string[] selected) => ObjectType("user", GraphSchemaCatalogue.UserAttributes, selected);

    protected static ConnectedSystemObjectType Groups(params string[] selected) => ObjectType("group", GraphSchemaCatalogue.GroupAttributes, selected);

    /// <summary>An Object Type as schema import leaves it: every catalogue attribute, with id, sourceOfAuthority and those named selected.</summary>
    private protected static ConnectedSystemObjectType ObjectType(string name, IReadOnlyList<GraphSchemaAttribute> catalogue, string[] selected) => new()
    {
        Name = name,
        Selected = true,
        Attributes = catalogue.Select(a => new ConnectedSystemObjectTypeAttribute
        {
            Name = a.Name,
            Type = a.Type,
            AttributePlurality = a.Plurality,
            Selected = a.Name is "id" or "sourceOfAuthority" || selected.Contains(a.Name),
            IsExternalId = a.Name == "id"
        }).ToList()
    };

    private protected StubHttpMessageHandler.RecordedRequest UserListRequest() =>
        Tenant.GraphRequests.First(r => r.RequestUri!.AbsolutePath == "/v1.0/users" && Query(r, "$filter") == null);

    private protected static string? Query(StubHttpMessageHandler.RecordedRequest request, string name) =>
        HttpUtility.ParseQueryString(request.RequestUri!.Query)[name];

    protected static List<string> Values(ConnectedSystemImportObject importObject, string name) =>
        importObject.Attributes.SingleOrDefault(a => a.Name == name)?.StringValues ?? [];
}
