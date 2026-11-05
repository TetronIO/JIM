// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Exchange;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Turning Exchange recipient management off holds delivery rather than failing it (#2021, decided 2026-11-05): every
/// settings save records whether the Connected System manages Exchange recipients now, from the Connector, so the
/// delivery lane can leave a held system's changes waiting; and a save that leaves changes waiting says so.
/// </summary>
[TestFixture]
public class ConnectedSystemExchangeHoldTests
{
    private Mock<ISyncRepository> _sync = null!;

    [SetUp]
    public void SetUp()
    {
        _sync = new Mock<ISyncRepository>();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ValidateConnectedSystemSettingsAsync_ExchangeConnector_RecordsWhetherTheSystemManagesExchangeRecipientsNowAsync(bool managing)
    {
        var connectedSystem = CreateConnectedSystem(managesNow: !managing);

        await Jim(new StubExchangeConnector(managing)).ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        Assert.That(connectedSystem.ManagesExchangeRecipients, Is.EqualTo(managing));
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_ConnectorWithoutExchange_RecordsThatItDoesNotManageExchangeRecipientsAsync()
    {
        var connectedSystem = CreateConnectedSystem(managesNow: true);

        await Jim(new StubPlainConnector()).ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        Assert.That(connectedSystem.ManagesExchangeRecipients, Is.False);
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_NotManagingWithChangesWaiting_WarnsThatDeliveryIsHeldAsync()
    {
        _sync.Setup(s => s.CountWaitingExchangeChangesAsync(7)).ReturnsAsync(3);
        var connectedSystem = CreateConnectedSystem(managesNow: true);

        var results = await Jim(new StubExchangeConnector(managing: false)).ConnectedSystems.ValidateConnectedSystemSettingsAsync(connectedSystem);

        var warning = results.Single(r => r.IsWarning);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.ErrorMessage, Does.Contain("held").And.Contain("3 changes"));
            Assert.That(results.All(r => r.IsValid), Is.True, "holding delivery is a warning, never a refusal");
        }
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_NotManagingWithNothingWaiting_SaysNothingAsync()
    {
        _sync.Setup(s => s.CountWaitingExchangeChangesAsync(7)).ReturnsAsync(0);

        var results = await Jim(new StubExchangeConnector(managing: false)).ConnectedSystems.ValidateConnectedSystemSettingsAsync(CreateConnectedSystem(managesNow: true));

        Assert.That(results.Where(r => r.IsWarning), Is.Empty);
    }

    [Test]
    public async Task ValidateConnectedSystemSettingsAsync_Managing_NeverCountsWhatIsWaitingAsync()
    {
        await Jim(new StubExchangeConnector(managing: true)).ConnectedSystems.ValidateConnectedSystemSettingsAsync(CreateConnectedSystem(managesNow: true));

        _sync.Verify(s => s.CountWaitingExchangeChangesAsync(It.IsAny<int>()), Times.Never);
    }

    private JimApplication Jim(IConnector connector)
    {
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Sync).Returns(_sync.Object);
        return new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(connector));
    }

    private static ConnectedSystem CreateConnectedSystem(bool managesNow)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub" };
        var setting = new ConnectorDefinitionSetting { Id = 1, Name = "Server", Type = ConnectedSystemSettingType.String };
        connectorDefinition.Settings.Add(setting);
        return new ConnectedSystem
        {
            Id = 7,
            Name = "PANOPLY",
            ConnectorDefinition = connectorDefinition,
            ManagesExchangeRecipients = managesNow,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "dc1.panoply.local" }]
        };
    }

    private sealed class StubPlainConnector : IConnector
    {
        public string Name => "Stub";
        public string? Description => null;
        public string? Url => null;
    }

    private sealed class StubExchangeConnector(bool managing) : IConnector, IConnectorExchangeRecipients
    {
        public string Name => "Stub";
        public string? Description => null;
        public string? Url => null;

        public bool IsManagingExchangeRecipients(IList<ConnectedSystemSettingValue> settings) => managing;

        public Task OpenExchangeSessionAsync(IList<ConnectedSystemSettingValue> settings, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ExchangeConvergeResult> ConvergeAsync(ExchangeRecipientTarget target, ExchangeDeclaredState declared, CancellationToken cancellationToken) =>
            Task.FromResult(ExchangeConvergeResult.Success(changedAnything: false));

        public ExchangeSessionStatistics GetSessionStatistics() => ExchangeSessionStatistics.None;

        public void CloseExchangeSession()
        {
        }
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
