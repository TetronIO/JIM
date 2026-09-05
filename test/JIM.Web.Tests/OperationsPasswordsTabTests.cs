// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Password Synchronisation queue now that it is the Passwords tab of Operations (#1635) rather than a
/// page of its own. What these pin is what the move could silently break: the queue still renders its summary and
/// its rows from the application, and the deep links other pages carry (<c>metaverseObjectId</c> from a person's
/// own page, <c>connectedSystemId</c> from a system's) still narrow the list when they arrive on the Operations
/// route, where the tab strip's own <c>?t=</c> parameter sits beside them.
/// </summary>
[TestFixture]
public class OperationsPasswordsTabTests : JimComponentTestContext
{
    private Mock<ISyncRepository> _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _syncRepository = new Mock<ISyncRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object, syncRepository: _syncRepository.Object)));
        // The tab resolves the signed-in administrator only when an action runs; rendering needs the provider to
        // exist, not to answer.
        Services.AddSingleton<AuthenticationStateProvider>(new AnonymousAuthenticationStateProvider());
    }

    /// <summary>
    /// NUnit reuses one fixture instance across the fixture and bUnit builds its service provider once, so the
    /// mocks are shared; reset them or arrangements and recorded calls leak between tests.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _syncRepository.Reset();
        _connectedSystemRepository.Reset();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync(
        [
            new ConnectedSystemHeader { Id = 3, Name = "Corporate Directory" },
            new ConnectedSystemHeader { Id = 4, Name = "Contractor LDAP" }
        ]);
        _syncRepository.Setup(r => r.GetPasswordQueueSummaryAsync(It.IsAny<DateTime>())).ReturnsAsync(new PasswordQueueSummary());
        ArrangeWindow([]);

        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        // The tab holds a grid that talks to JavaScript; NUnit reuses the fixture instance across tests, so each
        // rendered component is disposed here rather than left running into the next test.
        await DisposeComponentsAsync();
    }

    private void ArrangeWindow(List<PendingPasswordChangeHeader> rows) =>
        _syncRepository
            .Setup(r => r.GetPendingPasswordChangeHeadersAsync(
                It.IsAny<PendingPasswordChangeFilter>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<PendingPasswordChangeHeader> { Results = [.. rows], TotalResults = rows.Count });

    private static PendingPasswordChangeHeader Change(
        string who,
        string system,
        PendingPasswordChangeStatus status = PendingPasswordChangeStatus.Pending,
        string? targetMessage = null) => new()
    {
        Id = Guid.NewGuid(),
        MetaverseObjectId = Guid.NewGuid(),
        MetaverseObjectDisplayName = who,
        MetaverseObjectTypePluralName = "Users",
        ConnectedSystemId = 3,
        ConnectedSystemName = system,
        Status = status,
        FailureReason = status == PendingPasswordChangeStatus.Parked ? PasswordSetFailureReason.PolicyRejection : null,
        TargetMessage = targetMessage,
        AttemptCount = status == PendingPasswordChangeStatus.Parked ? 3 : 0,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(7)
    };

    [Test]
    public void OperationsPasswordsTab_Rendered_ShowsTheSummaryCountsAndTheQueuedChanges()
    {
        _navigation.NavigateTo("/admin/operations?t=passwords");
        _syncRepository.Setup(r => r.GetPasswordQueueSummaryAsync(It.IsAny<DateTime>())).ReturnsAsync(new PasswordQueueSummary
        {
            WaitingCount = 12,
            DueCount = 5,
            ParkedCount = 1400,
            ExpiredCount = 2,
            CancelledCount = 7
        });
        ArrangeWindow(
        [
            Change("Ada Lovelace", "Corporate Directory"),
            Change("Grace Hopper", "Contractor LDAP", PendingPasswordChangeStatus.Parked, "Too short.")
        ]);

        var cut = Render<OperationsPasswordsTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Waiting"));
                Assert.That(cut.Markup, Does.Contain("12"));
                Assert.That(cut.Markup, Does.Contain("5 due now"));
                Assert.That(cut.Markup, Does.Contain("1,400"), "the parked count is formatted, not raw");
                Assert.That(cut.Markup, Does.Contain("Ada Lovelace"));
                Assert.That(cut.Markup, Does.Contain("Grace Hopper"));
                Assert.That(cut.Markup, Does.Contain("Too short."),
                    "the target's own words are what point at the remedy, so they read inline with the status");
            }
        });
    }

    [Test]
    public void OperationsPasswordsTab_ARowBeingDelivered_ReadsAsDeliveringAndCannotBeRetried()
    {
        // A claimed row is on its way right now (#1635): it counts as waiting, it says so rather than "Due now",
        // and a retry would only ask for the attempt it is already getting, so that action is not offered. Cancel
        // still is: the delivery service's outcome write is guarded on the row still being Delivering.
        _navigation.NavigateTo("/admin/operations?t=passwords");
        ArrangeWindow([Change("Ada Lovelace", "Corporate Directory", PendingPasswordChangeStatus.Delivering)]);

        var cut = Render<OperationsPasswordsTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Delivering now"));
                Assert.That(cut.Markup, Does.Not.Contain("Due now"));
                Assert.That(cut.FindAll("[aria-label='Retry this password change']"), Is.Empty);
                Assert.That(cut.FindAll("[aria-label='Cancel this password change']"), Has.Count.EqualTo(1));
            }
        });
    }

    [Test]
    public void OperationsPasswordsTab_NothingQueued_SaysSoAsTheHealthyState()
    {
        _navigation.NavigateTo("/admin/operations?t=passwords");

        var cut = Render<OperationsPasswordsTab>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Nothing is waiting to be delivered")));
    }

    [Test]
    public void OperationsPasswordsTab_DeepLinkedToOneIdentity_NarrowsTheReadAndSaysSo()
    {
        var id = Guid.NewGuid();
        _navigation.NavigateTo($"/admin/operations?t=passwords&metaverseObjectId={id}");

        var cut = Render<OperationsPasswordsTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                _syncRepository.Verify(r => r.GetPendingPasswordChangeHeadersAsync(
                    It.Is<PendingPasswordChangeFilter>(f => f.MetaverseObjectId == id),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
                    Times.AtLeastOnce);
                Assert.That(cut.Markup, Does.Contain("Showing one identity's queued password changes"),
                    "the cards count the whole queue while the list is one person's; the notice is what reconciles them");
            }
        });
    }

    [Test]
    public void OperationsPasswordsTab_DeepLinkedToOneConnectedSystem_StartsWithThatSystemSelected()
    {
        _navigation.NavigateTo("/admin/operations?t=passwords&connectedSystemId=4");

        var cut = Render<OperationsPasswordsTab>();

        cut.WaitForAssertion(() => _syncRepository.Verify(r => r.GetPendingPasswordChangeHeadersAsync(
            It.Is<PendingPasswordChangeFilter>(f => f.ConnectedSystemId == 4),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.AtLeastOnce));
    }

    [Test]
    public void OperationsPasswordsTab_ShowingTheWholeQueue_DropsTheIdentityFromTheUrl()
    {
        // A tab is re-created each time the reader comes back to it and reads the query string afresh, so a
        // filter cleared only in memory would return on the next visit. The URL has to forget it too, and the
        // tab's own ?t= has to survive the forgetting.
        var id = Guid.NewGuid();
        _navigation.NavigateTo($"/admin/operations?t=passwords&metaverseObjectId={id}");
        var cut = Render<OperationsPasswordsTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Show the whole queue")));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Show the whole queue")).Click();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_navigation.Uri, Does.Not.Contain("metaverseObjectId"));
                Assert.That(_navigation.Uri, Does.Contain("t=passwords"));
                Assert.That(cut.Markup, Does.Not.Contain("Showing one identity's queued password changes"));
            }
        });
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
