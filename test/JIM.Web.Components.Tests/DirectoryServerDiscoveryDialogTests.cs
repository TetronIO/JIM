// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the Discover Domain Controllers dialog (issue #1167). The lifecycle behaviour under test is that a
/// discovery failure renders inside the dialog with the failure reason and a Retry action, never crashing the
/// page, and that closing without selecting anything reports no selection.
/// <para>
/// <see cref="ConnectedSystemServer"/> resolves Connectors through a fixed, non-injectable
/// <see cref="ConnectorFactory"/> (unlike <c>CertificateServer</c>, whose factory is a settable property for
/// exactly this reason), so these tests drive the dialog through the real File Connector, which does not
/// implement <c>IConnectorDirectoryServers</c> and so deterministically fails fast with no network I/O. That
/// exercises the same error/Retry/Close lifecycle a live directory failure would. The success path (a populated
/// table, selecting a row) needs a live Active Directory / Samba AD directory to discover against and so is not
/// covered here; it is exercised manually against the devcontainer's full stack.
/// </para>
/// </summary>
[TestFixture]
public class DirectoryServerDiscoveryDialogTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;
    private const string RetryButtonMarker = "jim-directory-server-discovery-retry";
    private const string CloseButtonMarker = "jim-directory-server-discovery-close";

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _jim = null!;

    /// <summary>
    /// Builds the mocked repository, the real <see cref="JimApplication"/> wrapping it, and registers a fake
    /// <see cref="IJimApplicationFactory"/> handing it out. Must happen here rather than in <c>[SetUp]</c>: see
    /// <see cref="JimComponentTestContext.ConfigureAdditionalServices"/>.
    /// </summary>
    protected override void ConfigureAdditionalServices()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _jim = new JimApplication(mockRepository.Object);
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()))
            .ReturnsAsync(CreateFileConnectorConnectedSystem());

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    private IRenderedComponent<MudDialogProvider> ShowDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DirectoryServerDiscoveryDialog>
        {
            { x => x.ConnectedSystemId, ConnectedSystemId }
        };
        provider.InvokeAsync(() => dialogService.ShowAsync<DirectoryServerDiscoveryDialog>("Discover Domain Controllers", parameters));
        provider.WaitForState(() => provider.FindAll(".mud-alert").Count > 0);

        return provider;
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_ConnectorWithoutTheCapability_RendersFailureReason()
    {
        var provider = ShowDialog();

        Assert.That(provider.Markup, Does.Contain("does not support directory server discovery"));
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_ConnectorWithoutTheCapability_NeverThrowsFromTheComponent()
    {
        // The whole point of the dialog's own try/catch: a discovery failure must render as a message, not
        // propagate and take the page down with it. Reaching the assertion above without bUnit's renderer
        // reporting an unhandled exception is itself the proof; this test names that expectation explicitly.
        Assert.DoesNotThrow(() => ShowDialog());
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_OnFailure_OffersRetry()
    {
        var provider = ShowDialog();

        Assert.That(provider.FindAll($"[data-testid='{RetryButtonMarker}']"), Is.Not.Empty);
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_RetryClicked_AttemptsDiscoveryAgain()
    {
        var provider = ShowDialog();
        _mockConnectedSystemRepo.Invocations.Clear();

        provider.Find($"[data-testid='{RetryButtonMarker}']").Click();
        provider.WaitForState(() => provider.FindAll(".mud-alert").Count > 0);

        _mockConnectedSystemRepo.Verify(
            r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>()),
            Times.AtLeast(1));
    }

    [Test]
    public void DirectoryServerDiscoveryDialog_CloseClicked_ReturnsNoSelection()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<DirectoryServerDiscoveryDialog>
        {
            { x => x.ConnectedSystemId, ConnectedSystemId }
        };

        IDialogReference? reference = null;
        provider.InvokeAsync(async () => reference = await dialogService.ShowAsync<DirectoryServerDiscoveryDialog>("Discover Domain Controllers", parameters));
        provider.WaitForState(() => provider.FindAll(".mud-alert").Count > 0);

        provider.Find($"[data-testid='{CloseButtonMarker}']").Click();

        var result = reference!.Result;
        Assert.That(result.IsCompleted, Is.True);
        Assert.That(result.Result!.Canceled, Is.True);
    }

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };
    }

    /// <summary>
    /// Hands out the same, already-arranged <see cref="JimApplication"/> instance on every call, since the
    /// component only needs one over the fixture's lifetime and the class under test disposes what it creates.
    /// </summary>
    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
