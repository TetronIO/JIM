// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Web.Pages.Admin;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A configuration-sized list page loads its list once, however many parts of the page ask for it. The page's own
/// initialisation and the grid's first window request both need the list, and the grid asks while the page is still
/// waiting on the database; each used to see "not loaded yet" and start a load of its own. On the Connected Systems
/// list that tripled the page's dominant cost, because its header query counts every Connected System Object (three
/// runs of a ~0.8s query per visit, measured at 1.5 million objects).
/// <para>
/// Each test holds the list's load open until the page has rendered, which is the window in which the grid asks.
/// </para>
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ConfigurationListSingleLoadTests : JimComponentTestContext
{
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    [Test]
    public void ConnectedSystemList_GridAsksForRowsWhileTheListIsStillLoading_LoadsTheHeadersOnce()
    {
        var load = HeldOpen<List<ConnectedSystemHeader>>();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).Returns(() => load.Task);

        var cut = Render<ConnectedSystemList>();
        load.SetResult([]);

        cut.WaitForAssertion(() =>
            _connectedSystemRepository.Verify(r => r.GetConnectedSystemHeadersAsync(), Times.Once));
    }

    [Test]
    public void ConnectorList_GridAsksForRowsWhileTheListIsStillLoading_LoadsTheHeadersOnce()
    {
        var load = HeldOpen<IList<ConnectorDefinitionHeader>>();
        _connectedSystemRepository.Setup(r => r.GetConnectorDefinitionHeadersAsync()).Returns(() => load.Task);

        var cut = Render<ConnectorList>();
        load.SetResult([]);

        cut.WaitForAssertion(() =>
            _connectedSystemRepository.Verify(r => r.GetConnectorDefinitionHeadersAsync(), Times.Once));
    }

    [Test]
    public void SyncRuleList_GridAsksForRowsWhileTheListIsStillLoading_LoadsTheHeadersOnce()
    {
        var load = HeldOpen<IList<SyncRuleHeader>>();
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>())).Returns(() => load.Task);

        var cut = Render<SyncRuleList>();
        load.SetResult([]);

        cut.WaitForAssertion(() =>
            _connectedSystemRepository.Verify(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>()), Times.Once));
    }

    private static TaskCompletionSource<T> HeldOpen<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
