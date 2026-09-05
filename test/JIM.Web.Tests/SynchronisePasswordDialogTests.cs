// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Synchronise Password dialog's delivering stage (#1635). Queueing used to be the end of the dialog's job; now
/// it stays open and shows one row per Connected System, updated through the outcome waiter, so an administrator
/// sees the password land (or be refused) rather than a snackbar saying it was queued.
/// <para>
/// The waiter is a fake the test scripts; the queueing itself runs through a real <see cref="JimApplication"/> over
/// mocked repositories, so the stage is reached the way the portal reaches it.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisePasswordDialogTests : JimComponentTestContext
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private const int UserObjectTypeId = 200;
    private const string SubmitMarker = "jim-sync-password-submit";
    private const string PasswordMarker = "jim-sync-password-password";
    private const string OutcomeMarker = "jim-sync-password-outcome";
    private const string DoneMarker = "jim-sync-password-done";
    private const string StillDeliveringMarker = "jim-sync-password-still-delivering";

    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private ScriptedWaiter _waiter = null!;
    private JimApplication _application = null!;
    private Guid _metaverseObjectId;

    protected override void ConfigureAdditionalServices()
    {
        _metaverseObjectId = Guid.NewGuid();
        var administratorId = Guid.NewGuid();

        var repository = new Mock<IRepository>();
        var metaverseRepo = new Mock<IMetaverseRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var createdActivities = new List<Activity>();

        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => createdActivities.FirstOrDefault(a => a.Id == id));
        activityRepo.Setup(r => r.GetPasswordSynchronisationOutcomesAsync(It.IsAny<Guid>())).ReturnsAsync([]);

        // The signed-in administrator and the person whose password it is are both Metaverse Objects.
        metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new MetaverseObject { Id = id, CachedDisplayName = id == administratorId ? "Admin" : "Ada Lovelace" });

        repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        repository.Setup(r => r.ServiceSettings).Returns(new Mock<IServiceSettingsRepository>().Object);

        // The queue itself is a mock: what a row looks like once written is the queue repository's concern, and the
        // dialog only needs the write to succeed and the waiter to describe what followed.
        _application = new JimApplication(repository.Object, syncRepository: new Mock<ISyncRepository>().Object);
        _waiter = new ScriptedWaiter();

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_application));
        Services.AddSingleton<IPasswordChangeOutcomeWaiter>(_waiter);
        Services.AddSingleton<AuthenticationStateProvider>(new SignedInAuthenticationStateProvider(administratorId));
    }

    [SetUp]
    public void SetUp()
    {
        _connectedSystemRepo.Reset();
        _waiter.Reset();
        ArrangeTargets();
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    /// <summary>
    /// Arranges Connected Systems that take synchronised passwords, and an account for the identity in each.
    /// </summary>
    private void ArrangeTargets(params (int Id, string Name)[] systems)
    {
        _connectedSystemRepo.Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(systems.Select(s => new PasswordSynchronisationTarget
            {
                ConnectedSystemId = s.Id,
                ConnectedSystemName = s.Name,
                TargetObjectTypeId = UserObjectTypeId,
                Enabled = true,
                TimeToLive = TimeSpan.FromDays(7)
            }).ToList());
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId))
            .ReturnsAsync(systems.Select(s => new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = s.Id,
                TypeId = UserObjectTypeId
            }).ToList());
    }

    private static PasswordChangeTargetOutcome Target(int id, string name, PasswordChangeTargetState state, string? message = null) => new()
    {
        ConnectedSystemId = id,
        ConnectedSystemName = name,
        State = state,
        Message = message,
        AttemptCount = state == PasswordChangeTargetState.Queued ? 0 : 1
    };

    private IRenderedComponent<MudDialogProvider> ShowDialog(int targetSystemCount, TimeSpan? deliveryWait = null)
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<SynchronisePasswordDialog>
        {
            { x => x.MetaverseObjectId, _metaverseObjectId },
            { x => x.DisplayName, "Ada Lovelace" },
            { x => x.TargetSystemCount, targetSystemCount }
        };
        if (deliveryWait.HasValue)
            parameters.Add(x => x.DeliveryWait, deliveryWait.Value);

        provider.InvokeAsync(() => dialogService.ShowAsync<SynchronisePasswordDialog>("Synchronise Password", parameters));
        provider.WaitForElement($"[data-testid='{SubmitMarker}']");
        return provider;
    }

    private static void TypePasswordAndSubmit(IRenderedComponent<MudDialogProvider> provider)
    {
        provider.Find($"input[data-testid='{PasswordMarker}']").Input("Correct-Horse-42");
        provider.WaitForAssertion(() => Assert.That(provider.Find($"[data-testid='{SubmitMarker}']").HasAttribute("disabled"), Is.False));
        provider.Find($"[data-testid='{SubmitMarker}']").Click();
    }

    [Test]
    public void SynchronisePasswordDialog_AfterQueueing_ShowsOneRowPerTargetWithItsOutcome()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"), (HrPortalId, "HR Portal"));
        _waiter.Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(new PasswordChangeOutcomes
        {
            IsSettled = true,
            Targets =
            [
                Target(CorporateAdId, "Corporate AD", PasswordChangeTargetState.Set, "Password set."),
                Target(HrPortalId, "HR Portal", PasswordChangeTargetState.Parked, "The password does not meet the length, complexity or history requirements.")
            ]
        });
        var provider = ShowDialog(targetSystemCount: 2);

        TypePasswordAndSubmit(provider);

        provider.WaitForAssertion(() =>
        {
            var rows = provider.FindAll($"[data-testid='{OutcomeMarker}']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rows, Has.Count.EqualTo(2));
                Assert.That(rows[0].GetAttribute("data-state"), Is.EqualTo("Set"));
                Assert.That(rows[1].GetAttribute("data-state"), Is.EqualTo("Parked"));
                Assert.That(provider.Markup, Does.Contain("Corporate AD"));
                Assert.That(provider.Markup, Does.Contain("Password set"));
                Assert.That(provider.Markup, Does.Contain("The password does not meet the length, complexity or history requirements."),
                    "the target's own words are the remedy, so they are shown in the row");
                Assert.That(provider.Markup, Does.Contain("Of 2 Connected Systems: password set on 1, 1 needs attention below."),
                    "the summary line accounts for every target by state rather than calling a parked one \"on its way\"");
                Assert.That(provider.FindAll($"[data-testid='{DoneMarker}']"), Has.Count.EqualTo(1));
                Assert.That(provider.FindAll($"[data-testid='{SubmitMarker}']"), Is.Empty, "the dialog is past queueing; nothing more to submit");
                Assert.That(provider.FindAll($"[data-testid='{StillDeliveringMarker}']"), Is.Empty, "every target settled, so there is nothing still to follow");
            }
        });
    }

    [Test]
    public void SynchronisePasswordDialog_WaitRunsOut_SaysDeliveryContinuesAndWhereToFollowIt()
    {
        ArrangeTargets((CorporateAdId, "Corporate AD"));
        _waiter.Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(new PasswordChangeOutcomes
        {
            IsSettled = false,
            Targets = [Target(CorporateAdId, "Corporate AD", PasswordChangeTargetState.Queued)]
        });
        var provider = ShowDialog(targetSystemCount: 1, deliveryWait: TimeSpan.FromMilliseconds(300));

        TypePasswordAndSubmit(provider);

        // The note arrives once the delivery wait runs out, so wait for it before reading the rest of the stage.
        provider.WaitForElement($"[data-testid='{StillDeliveringMarker}']");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("Of 1 Connected System: 1 still on its way."));
            Assert.That(provider.Markup, Does.Contain($"metaverseObjectId={_metaverseObjectId}"),
                "the note links to this person's rows on the Passwords tab of Operations");
            Assert.That(provider.FindAll($"[data-testid='{OutcomeMarker}']")[0].GetAttribute("data-state"), Is.EqualTo("Queued"));
            Assert.That(provider.FindAll($"[data-testid='{DoneMarker}']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void SynchronisePasswordDialog_NoTargets_ClosesWithoutADeliveringStage()
    {
        // Requirement 14: a change that reached nothing says so (the snackbar), and there are no rows to follow.
        var provider = ShowDialog(targetSystemCount: 0);

        TypePasswordAndSubmit(provider);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.FindAll($"[data-testid='{SubmitMarker}']"), Is.Empty, "the dialog closed");
                Assert.That(provider.FindAll($"[data-testid='{OutcomeMarker}']"), Is.Empty);
                Assert.That(_waiter.Calls, Is.Zero, "nothing was queued, so there is nothing to wait for");
            }
        });
    }

    [Test]
    public void SynchronisePasswordDialog_WhileDelivering_ShowsTheQueuedRowsBeforeTheWaiterAnswers()
    {
        // The rows appear from what was queued, so the stage is never empty while the first wait is in flight.
        ArrangeTargets((CorporateAdId, "Corporate AD"));
        var release = new TaskCompletionSource<PasswordChangeOutcomes?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiter.Answer = _ => release.Task;
        var provider = ShowDialog(targetSystemCount: 1);

        TypePasswordAndSubmit(provider);

        provider.WaitForAssertion(() =>
        {
            var rows = provider.FindAll($"[data-testid='{OutcomeMarker}']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].GetAttribute("data-state"), Is.EqualTo("Queued"));
            }
        });

        release.SetResult(new PasswordChangeOutcomes
        {
            IsSettled = true,
            Targets = [Target(CorporateAdId, "Corporate AD", PasswordChangeTargetState.Set)]
        });

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindAll($"[data-testid='{OutcomeMarker}']")[0].GetAttribute("data-state"), Is.EqualTo("Set")));
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    /// <summary>
    /// A waiter the test scripts. <see cref="Answer"/> may return a task that is not yet complete, to hold the dialog
    /// in its delivering stage.
    /// </summary>
    private sealed class ScriptedWaiter : IPasswordChangeOutcomeWaiter
    {
        public Func<Guid, Task<PasswordChangeOutcomes?>> Answer { get; set; } = _ => Task.FromResult<PasswordChangeOutcomes?>(null);

        public int Calls { get; private set; }

        public void Reset()
        {
            Calls = 0;
            Answer = _ => Task.FromResult<PasswordChangeOutcomes?>(null);
        }

        public Task<PasswordChangeOutcomes?> WaitForOutcomesAsync(Guid activityId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            return Answer(activityId);
        }
    }

    private sealed class SignedInAuthenticationStateProvider(Guid metaverseObjectId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(Constants.BuiltInClaims.MetaverseObjectId, metaverseObjectId.ToString())], "Test"))));
    }
}
