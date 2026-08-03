// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Schema discovery shortfalls (a service provider publishing no schemas, an unparseable attribute definition)
/// must reach the administrator, not just the log: the Activity completes with a warning carrying the detail, and
/// the refresh result surfaces the same warnings so the portal can show them on the schema screen. These tests
/// hold both <c>ImportConnectedSystemSchemaAsync</c> overloads to that behaviour, and prove a warning-free import
/// still completes cleanly.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaImportWarningTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private StubSchemaConnector _connector = null!;
    private Activity? _capturedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        _capturedActivity = null;
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _capturedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repository.Object);

        _connector = new StubSchemaConnector();
        _jim.ConnectedSystems.ConnectorFactory = new StubConnectorFactory(_connector);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenDiscoveryReportsWarnings_CompletesTheActivityWithWarningAsync()
    {
        _connector.Schema.Warnings.Add("The service provider does not publish /Schemas; the core RFC 7643 schemas were assumed.");
        _connector.Schema.Warnings.Add("Attribute 'manager' on 'User' has an unrecognised type 'complex'; imported as text.");

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DiscoveryWarnings, Is.EqualTo(_connector.Schema.Warnings),
                "The refresh result must carry the discovery warnings so the portal can show them on the schema screen.");
            Assert.That(_capturedActivity, Is.Not.Null);
            Assert.That(_capturedActivity!.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning),
                "A schema import that discovered less than it should have must not present as an unqualified success.");
            Assert.That(_capturedActivity!.WarningMessage, Does.Contain("does not publish /Schemas"));
            Assert.That(_capturedActivity!.WarningMessage, Does.Contain("unrecognised type 'complex'"));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenDiscoveryReportsWarnings_ApiKeyOverloadBehavesTheSameAsync()
    {
        // Surface parity: an import run through the REST API or PowerShell must record the same warning outcome
        // as the same import run through the portal.
        _connector.Schema.Warnings.Add("The service provider does not publish /Schemas; the core RFC 7643 schemas were assumed.");

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewApiKey());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DiscoveryWarnings, Is.EqualTo(_connector.Schema.Warnings));
            Assert.That(_capturedActivity, Is.Not.Null);
            Assert.That(_capturedActivity!.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(_capturedActivity!.WarningMessage, Does.Contain("does not publish /Schemas"));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenDiscoveryReportsNoWarnings_CompletesTheActivityCleanlyAsync()
    {
        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DiscoveryWarnings, Is.Empty);
            Assert.That(_capturedActivity, Is.Not.Null);
            Assert.That(_capturedActivity!.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(_capturedActivity!.WarningMessage, Is.Null);
        }
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private static ApiKey NewApiKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test API Key"
    };

    private static ConnectedSystem CreateConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Schema Connector" };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test Stub System",
            ConnectorDefinition = connectorDefinition,
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }
            ]
        };
    }

    /// <summary>
    /// A schema-only connector whose discovery outcome the test controls, including the warnings it reports.
    /// </summary>
    private sealed class StubSchemaConnector : IConnector, IConnectorSchema
    {
        public string Name => "Stub Schema Connector";

        public string? Description => null;

        public string? Url => null;

        public ConnectorSchema Schema { get; } = CreateSchema();

        private static ConnectorSchema CreateSchema()
        {
            var userName = new ConnectorSchemaAttribute("userName", AttributeDataType.Text, AttributePlurality.SingleValued);
            var objectType = new ConnectorSchemaObjectType("User") { RecommendedExternalIdAttribute = userName };
            objectType.Attributes.Add(userName);
            return new ConnectorSchema { ObjectTypes = [objectType] };
        }

        public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger) => Task.FromResult(Schema);
    }

    /// <summary>
    /// Resolves every connector name to the supplied stub, so the server under test exercises its own schema
    /// handling rather than a real connector's.
    /// </summary>
    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
