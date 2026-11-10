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
using JIM.Models.Transactional.DTOs;
using JIM.TestSupport;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Retry offered wherever a queued Exchange change shows (#2021, Phase 6b): it retries exactly the change it was
/// rendered for, as the signed-in administrator, tells its host so the host can re-read, and is not offered at all while
/// Exchange recipients are switched off.
/// </summary>
[TestFixture]
public class ExchangeChangeRetryButtonTests : JimComponentTestContext
{
    private static readonly Guid AdministratorId = Guid.NewGuid();

    private Mock<IRepository> _repository = null!;
    private Mock<ISyncRepository> _syncRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;

    protected override void ConfigureAdditionalServices()
    {
        _repository = new Mock<IRepository>();
        _syncRepository = new Mock<ISyncRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(_repository.Object, syncRepository: _syncRepository.Object)));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAuthenticationStateProvider());
    }

    [SetUp]
    public void SetUp()
    {
        _syncRepository.Reset();
        _activityRepository.Reset();
        _metaverseRepository.Reset();
        _repository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        _metaverseRepository.Setup(r => r.GetMetaverseObjectAsync(AdministratorId))
            .ReturnsAsync(new MetaverseObject { Id = AdministratorId, CachedDisplayName = "Jay" });
        _syncRepository.Setup(r => r.RetryExchangeChangesAsync(It.IsAny<PendingExchangeChangeFilter>()))
            .ReturnsAsync([new RetriedExchangeChange { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid(), ConnectedSystemName = "Panoply AD" }]);
    }

    [Test]
    public async Task Clicked_RetriesThatChangeAsTheAdministratorAndTellsTheHostAsync()
    {
        var changeId = Guid.NewGuid();
        var retried = false;
        var cut = Render<ExchangeChangeRetryButton>(p => p
            .Add(b => b.ChangeId, changeId)
            .Add(b => b.OnRetried, () => retried = true));

        await cut.WaitForElement("[data-testid='jim-exchange-retry']").ClickAsync();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                _syncRepository.Verify(r => r.RetryExchangeChangesAsync(It.Is<PendingExchangeChangeFilter>(f => f.Ids!.Single() == changeId && f.TargetsSpecificChanges)), Times.Once);
                _activityRepository.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
                    a.TargetOperationType == ActivityTargetOperationType.RetryExchangeRecipient && a.InitiatedById == AdministratorId)), Times.Once);
                Assert.That(retried, Is.True);
            }
        });
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
