// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging.DTOs;
using JIM.TestSupport;
using JIM.Web.Pages.Admin;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Connected Systems list loads its headers once, however many parts of the page ask for them. The page's own
/// initialisation and the grid's first window request both need the list, and the grid asks while the page is still
/// waiting on the database; each used to see "not loaded yet" and start a load of its own. The header query counts
/// every Connected System Object, so at scale that tripled the page's dominant cost (three runs of a ~0.8s query per
/// visit, measured at 1.5 million objects).
/// </summary>
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class ConnectedSystemListPageTests : JimComponentTestContext
{
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private TaskCompletionSource<List<ConnectedSystemHeader>> _headersLoad = null!;

    [SetUp]
    public void SetUp()
    {
        _headersLoad = new TaskCompletionSource<List<ConnectedSystemHeader>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).Returns(() => _headersLoad.Task);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    [Test]
    public void Render_GridAsksForRowsWhileTheListIsStillLoading_LoadsTheHeadersOnce()
    {
        var cut = Render<ConnectedSystemList>();

        // Hold the load open until the grid has had its chance to ask for its first window, then let it finish.
        _headersLoad.SetResult([]);

        cut.WaitForAssertion(() =>
            _connectedSystemRepository.Verify(r => r.GetConnectedSystemHeadersAsync(), Times.Once));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
