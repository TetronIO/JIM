// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.TestSupport;
using JIM.Web.Pages.Admin.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Exchange tab of Operations (#2021, Phase 6b): the queue's summary and rows, Retry offered exactly where a retry
/// would act, and "Retry shown" acting on the changes the list shows, its search text included (#2725).
/// </summary>
[TestFixture]
public class OperationsExchangeTabTests : JimComponentTestContext
{
    private static readonly Guid AdministratorId = Guid.NewGuid();

    private Mock<IRepository> _repository = null!;
    private Mock<ISyncRepository> _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        _repository = new Mock<IRepository>();
        _syncRepository = new Mock<ISyncRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _repository.Setup(r => r.Activity).Returns(new Mock<IActivityRepository>().Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(_repository.Object, syncRepository: _syncRepository.Object)));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAuthenticationStateProvider());
    }

    [SetUp]
    public void SetUp()
    {
        _syncRepository.Reset();
        _connectedSystemRepository.Reset();
        _metaverseRepository.Reset();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(
            new Dictionary<int, string> { [3] = "Panoply AD", [4] = "Resurgam AD" });
        _metaverseRepository.Setup(r => r.GetMetaverseObjectAsync(AdministratorId))
            .ReturnsAsync(new MetaverseObject { Id = AdministratorId, CachedDisplayName = "Jay" });
        _syncRepository.Setup(r => r.GetExchangeQueueSummaryAsync(It.IsAny<DateTime>())).ReturnsAsync(new ExchangeQueueSummary());
        _syncRepository.Setup(r => r.RetryExchangeChangesAsync(It.IsAny<PendingExchangeChangeFilter>())).ReturnsAsync([]);
        ArrangeWindow([]);

        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [Test]
    public void Rendered_ShowsTheSummaryAndEachChangeWithExchangesReason()
    {
        _navigation.NavigateTo("/admin/operations?t=exchange");
        _syncRepository.Setup(r => r.GetExchangeQueueSummaryAsync(It.IsAny<DateTime>())).ReturnsAsync(new ExchangeQueueSummary
        {
            QueuedCount = 4, RetryingCount = 1, ParkedCount = 1200, RefusedCount = 2, NotMadeCount = 1, DueCount = 3
        });
        ArrangeWindow(
        [
            Change("Ada Lovelace", PendingExchangeChangeStatus.Parked, "The proxy address is already used by another recipient."),
            Change("Grace Hopper", PendingExchangeChangeStatus.Pending)
        ]);

        var cut = Render<OperationsExchangeTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("1,200"), "the parked count is formatted, not raw");
                Assert.That(cut.Markup, Does.Contain("Ada Lovelace"));
                Assert.That(cut.Markup, Does.Contain("Grace Hopper"));
                Assert.That(cut.Markup, Does.Contain("The proxy address is already used by another recipient."),
                    "Exchange's own words are what point at the remedy");
            }
        });
    }

    [Test]
    public void Rendered_OffersRetryOnlyWhereARetryWouldAct()
    {
        _navigation.NavigateTo("/admin/operations?t=exchange");
        ArrangeWindow(
        [
            Change("Ada Lovelace", PendingExchangeChangeStatus.Parked, "Access denied."),
            Change("Grace Hopper", PendingExchangeChangeStatus.Pending, attempted: true),
            Change("Alan Turing", PendingExchangeChangeStatus.Pending),
            Change("Katherine Johnson", PendingExchangeChangeStatus.Delivering),
            Change("Edsger Dijkstra", PendingExchangeChangeStatus.Refused, "A mailbox cannot be created on a contact.")
        ]);

        var cut = Render<OperationsExchangeTab>();

        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[aria-label='Retry this Exchange change']"), Has.Count.EqualTo(2),
            "parked and retrying; queued is already due, delivering is being applied, refused waits on a new declared state"));
    }

    [Test]
    public async Task RetryShown_ActsOnTheChangesTheListShowsSearchTextIncludedAsync()
    {
        _navigation.NavigateTo("/admin/operations?t=exchange&q=resurgam");
        ArrangeWindow([Change("Alan Turing", PendingExchangeChangeStatus.Parked, "Access denied.")]);
        var provider = Render<MudDialogProvider>();
        var cut = Render<OperationsExchangeTab>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Alan Turing")));

        // The handler waits for the confirmation's answer, so its click completes only once that is given.
        var retrying = cut.FindAll("button").Single(b => b.TextContent.Contains("Retry shown")).ClickAsync();
        provider.WaitForAssertion(() => Assert.That(provider.FindAll("button").Count(b => b.TextContent.Trim() == "Retry"), Is.EqualTo(1)));
        await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Retry").ClickAsync();
        await retrying;

        cut.WaitForAssertion(() => _syncRepository.Verify(r => r.RetryExchangeChangesAsync(
            It.Is<PendingExchangeChangeFilter>(f => f.SearchText == "resurgam" && !f.TargetsSpecificChanges)), Times.Once));
    }

    [Test]
    public async Task RetryOne_NamesJustThatChangeAsync()
    {
        _navigation.NavigateTo("/admin/operations?t=exchange");
        var parked = Change("Ada Lovelace", PendingExchangeChangeStatus.Parked, "Access denied.");
        ArrangeWindow([parked]);
        var cut = Render<OperationsExchangeTab>();

        await cut.WaitForElement("[aria-label='Retry this Exchange change']").ClickAsync();

        cut.WaitForAssertion(() => _syncRepository.Verify(r => r.RetryExchangeChangesAsync(
            It.Is<PendingExchangeChangeFilter>(f => f.Ids!.Single() == parked.Id && f.SearchText == null)), Times.Once));
    }

    [Test]
    public void NothingQueued_SaysSoAsTheHealthyState()
    {
        _navigation.NavigateTo("/admin/operations?t=exchange");

        var cut = Render<OperationsExchangeTab>();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Nothing is waiting for Exchange")));
    }

    private void ArrangeWindow(List<PendingExchangeChangeHeader> rows) =>
        _syncRepository
            .Setup(r => r.GetPendingExchangeChangeHeadersAsync(
                It.IsAny<PendingExchangeChangeFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<PendingExchangeChangeHeader> { Results = [.. rows], TotalResults = rows.Count });

    private static PendingExchangeChangeHeader Change(string who, PendingExchangeChangeStatus status, string? message = null, bool attempted = false)
    {
        var failed = status is PendingExchangeChangeStatus.Parked || attempted;
        return new PendingExchangeChangeHeader
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ConnectedSystemId = 3,
            ConnectedSystemName = "Panoply AD",
            ConnectedSystemObjectId = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            MetaverseObjectDisplayName = who,
            MetaverseObjectTypePluralName = "Users",
            RecipientType = "Mailbox",
            Status = status,
            FailureKind = failed ? ExchangeChangeFailureKind.Refused : null,
            FailureMessage = message,
            AttemptCount = failed ? 1 : 0,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            LastAttemptedAt = failed ? DateTime.UtcNow.AddMinutes(-5) : null,
            NextRetryAt = attempted ? DateTime.UtcNow.AddMinutes(5) : null,
            ChangeActivityStatus = status == PendingExchangeChangeStatus.Refused ? ActivityStatus.CompleteWithError : ActivityStatus.Complete
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    private sealed class SignedInAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(Constants.BuiltInClaims.MetaverseObjectId, AdministratorId.ToString())], "Test"))));
    }
}
