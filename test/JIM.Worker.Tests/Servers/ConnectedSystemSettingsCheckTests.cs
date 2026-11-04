// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What validating a Connected System's settings asks of its Connector, and what comes back (#2021, D5). A Connector
/// can warn about a setting without refusing it: the save goes ahead and the administrator is told. A Connector with a
/// check too slow for the quick validation (signing in to Exchange, for one) is asked only once the quick checks pass,
/// so a form with a missing value never waits on a network round trip it was always going to fail.
/// </summary>
[TestFixture]
public class ConnectedSystemSettingsCheckTests
{
    private StubCheckingConnector _connector = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _connector = new StubCheckingConnector();
        _jim = new JimApplication(new Mock<IRepository>().Object, connectorFactory: new StubConnectorFactory(_connector));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _connector?.Dispose();
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_ConnectorWarns_ReturnsTheWarningWithoutRefusingAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        _connector.QuickResults.Add(ConnectorSettingValueValidationResult.Warning(connectedSystem.SettingValues[0], "Only some of this will work."));

        var results = await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        var warning = results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.IsValid, Is.True, "a warning never refuses a save");
            Assert.That(warning.Severity, Is.EqualTo(ConnectorSettingValidationSeverity.Warning));
            Assert.That(warning.ErrorMessage, Is.EqualTo("Only some of this will work."));
        }
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_QuickChecksPass_RunsTheSettingsCheckAndReturnsWhatItFindsAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        _connector.CheckResults.Add(ConnectorSettingValueValidationResult.Warning(connectedSystem.SettingValues[0], "The account cannot run Enable-Mailbox."));

        var results = await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.ChecksRun, Is.EqualTo(1));
            Assert.That(results.Select(r => r.ErrorMessage), Is.EqualTo(new[] { "The account cannot run Enable-Mailbox." }));
        }
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_ConnectorRefusesQuickly_DoesNotRunTheSettingsCheckAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        _connector.QuickResults.Add(new ConnectorSettingValueValidationResult { IsValid = false, ErrorMessage = "The directory refused the bind." });

        var results = await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.ChecksRun, Is.Zero, "the slow check would only fail for the same reason, later");
            Assert.That(results.Single().ErrorMessage, Is.EqualTo("The directory refused the bind."));
        }
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_RequiredValueMissing_DoesNotRunTheSettingsCheckAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.SettingValues[0].StringValue = null;

        var results = await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connector.ChecksRun, Is.Zero);
            Assert.That(results.Any(r => !r.IsValid), Is.True);
        }
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_QuickCheckOnlyWarns_StillRunsTheSettingsCheckAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        _connector.QuickResults.Add(ConnectorSettingValueValidationResult.Warning(null, "A quick warning."));

        await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        Assert.That(_connector.ChecksRun, Is.EqualTo(1), "a warning is not a refusal, so it does not stop the check");
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_Always_DisposesTheConnectorAfterTheCheckAsync()
    {
        await _jim.ConnectedSystems.ValidateConnectedSystemSettingsAsync(CreateConnectedSystem());

        Assert.That(_connector.DisposedAfterCheck, Is.True, "the check holds a session; it must finish before the Connector is disposed");
    }

    private static ConnectedSystem CreateConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = StubCheckingConnector.ConnectorName };
        var setting = new ConnectorDefinitionSetting { Id = 1, Name = "Server", Type = ConnectedSystemSettingType.String, Required = true };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = 7,
            Name = "PANOPLY",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "dc1.panoply.local" }]
        };
    }

    /// <summary>A Connector with both kinds of validation, returning whatever the test arranges.</summary>
    private sealed class StubCheckingConnector : IConnector, IConnectorSettings, IConnectorSettingsCheck, IDisposable
    {
        public const string ConnectorName = "Stub Checking Connector";

        public string Name => ConnectorName;
        public string? Description => null;
        public string? Url => null;

        public List<ConnectorSettingValueValidationResult> QuickResults { get; } = [];
        public List<ConnectorSettingValueValidationResult> CheckResults { get; } = [];
        public int ChecksRun { get; private set; }
        public bool DisposedAfterCheck { get; private set; }

        public List<ConnectorSetting> GetSettings() => [];

        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settings, ILogger logger) => QuickResults;

        public async Task<ConnectorSettingsCheckResult> CheckSettingsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger, CancellationToken cancellationToken)
        {
            await Task.Yield();
            ChecksRun++;
            return new ConnectorSettingsCheckResult { Results = CheckResults };
        }

        public void Dispose() => DisposedAfterCheck = ChecksRun > 0;
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
