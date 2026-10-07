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
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web.Pages.Admin.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Generated Values tab of Operations (Unique Value Generation, #242, release 4, Phase 9): which tiles, rows and
/// actions show under which condition, and that the deep links the attention indicators carry narrow the list. Asserts on
/// the component's own markers and state, not on wording.
/// </summary>
[TestFixture]
public class OperationsGeneratedValuesTabTests : JimComponentTestContext
{
    private const int CorporateAd = 2;
    private const int ContractorLdap = 3;
    private const int HrImportRule = 10;

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
        Services.AddSingleton<AuthenticationStateProvider>(new AnonymousAuthenticationStateProvider());
    }

    [SetUp]
    public void SetUp()
    {
        _syncRepository.Reset();
        _connectedSystemRepository.Reset();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync(
        [
            new ConnectedSystemHeader { Id = CorporateAd, Name = "Corporate AD" },
            new ConnectedSystemHeader { Id = ContractorLdap, Name = "Contractor LDAP" }
        ]);
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>())).ReturnsAsync(
            new List<SyncRuleHeader> { new() { Id = HrImportRule, Name = "HR Import", ConnectedSystemName = "HR" } });
        _syncRepository.Setup(r => r.GetAllSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync(new List<JIM.Models.Logic.SyncRule>());
        ArrangeCounts();
        ArrangeWindow([]);

        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private void ArrangeCounts(int needsDecision = 0, int renameAllowed = 0, int corrected = 0) =>
        _syncRepository.Setup(r => r.GetGeneratedValueDecisionCountsAsync(It.IsAny<DateTime>())).ReturnsAsync(
            needsDecision + renameAllowed + corrected == 0
                ? []
                : [new GeneratedValueDecisionCount { GenerationId = 7, NeedsDecisionCount = needsDecision, RenameAllowedCount = renameAllowed, CorrectedCount = corrected }]);

    private void ArrangeWindow(List<GeneratedValueDecisionHeader> rows) =>
        _syncRepository
            .Setup(r => r.GetGeneratedValueDecisionHeadersAsync(It.IsAny<GeneratedValueDecisionQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(() => new RangeResultSet<GeneratedValueDecisionHeader> { Results = [.. rows], TotalResults = rows.Count });

    private static GeneratedValueDecisionHeader Decision(string who, GeneratedValueDecisionStatus status) => new()
    {
        AssignmentId = Guid.NewGuid(),
        Status = status,
        MetaverseObjectId = Guid.NewGuid(),
        MetaverseObjectDisplayName = who,
        MetaverseObjectTypeName = "User",
        MetaverseObjectTypePluralName = "Users",
        AttributeName = "Account Name",
        Value = "r.okafor",
        Reason = GeneratedValueNeedsDecisionReason.AnchoredElsewhere,
        RejectedByConnectedSystemId = ContractorLdap,
        RejectedByConnectedSystemName = "Contractor LDAP",
        AnchoredByConnectedSystemId = CorporateAd,
        AnchoredByConnectedSystemName = "Corporate AD",
        Since = DateTime.UtcNow.AddHours(-2),
        RenameAllowedAt = status == GeneratedValueDecisionStatus.RenameAllowed ? DateTime.UtcNow.AddMinutes(-10) : null,
        RenameAllowedBy = status == GeneratedValueDecisionStatus.RenameAllowed ? "Jay" : null,
        SyncRuleId = HrImportRule,
        SyncRuleName = "HR Import"
    };

    private static string TileValue(IRenderedComponent<OperationsGeneratedValuesTab> cut, string tile) =>
        cut.Find($"[data-testid='generated-values-tile-{tile}'] [data-testid='generated-values-tile-count']").TextContent.Trim();

    [Test]
    public void Render_Summary_ShowsTheThreeCounts()
    {
        _navigation.NavigateTo("/admin/operations?t=generated-values");
        ArrangeCounts(needsDecision: 3, renameAllowed: 1, corrected: 1400);

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(TileValue(cut, "needs-decision"), Is.EqualTo("3"));
                Assert.That(TileValue(cut, "rename-allowed"), Is.EqualTo("1"));
                Assert.That(TileValue(cut, "corrected"), Is.EqualTo("1,400"));
            }
        });
    }

    [Test]
    public void Render_HeldValueAndAllowedRename_OnlyTheHeldValueOffersItsActions()
    {
        _navigation.NavigateTo("/admin/operations?t=generated-values");
        ArrangeCounts(needsDecision: 1, renameAllowed: 1);
        ArrangeWindow([Decision("Rita Okafor", GeneratedValueDecisionStatus.NeedsDecision), Decision("Ana Ruiz", GeneratedValueDecisionStatus.RenameAllowed)]);

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("Rita Okafor").And.Contain("Ana Ruiz"));
                Assert.That(cut.FindAll("[data-testid='generated-value-allow-rename']"), Has.Count.EqualTo(1));
                Assert.That(cut.FindAll("[data-testid='generated-value-try-again']"), Has.Count.EqualTo(1));
                Assert.That(cut.FindAll("[data-testid='generated-value-why-rename-allowed']"), Has.Count.EqualTo(1));
            }
        });
    }

    [Test]
    public void Render_NothingNeedsADecision_TheBulkTryAgainIsDisabled()
    {
        _navigation.NavigateTo("/admin/operations?t=generated-values");
        ArrangeCounts(renameAllowed: 1);
        ArrangeWindow([Decision("Ana Ruiz", GeneratedValueDecisionStatus.RenameAllowed)]);

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Find("[data-testid='generated-values-try-again-all']").HasAttribute("disabled"), Is.True,
                "an allowed rename is not released by Try again, so there is nothing for it to do"));
    }

    [Test]
    public void Render_ValuesNeedADecision_TheBulkTryAgainIsOffered()
    {
        _navigation.NavigateTo("/admin/operations?t=generated-values");
        ArrangeCounts(needsDecision: 3);
        ArrangeWindow([Decision("Rita Okafor", GeneratedValueDecisionStatus.NeedsDecision)]);

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("[data-testid='generated-values-try-again-all']");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(button.HasAttribute("disabled"), Is.False);
                Assert.That(button.TextContent, Does.Contain("3"));
            }
        });
    }

    [Test]
    public void Render_DeepLinkedToARuleAndASystem_StartsWithBothSelected()
    {
        _navigation.NavigateTo($"/admin/operations?t=generated-values&syncRuleId={HrImportRule}&connectedSystemId={CorporateAd}");

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindComponents<MudSelect<int?>>().Select(s => s.Instance.GetState(x => x.Value)), Is.EqualTo(new int?[] { CorporateAd, HrImportRule }));
                Assert.That(cut.Instance.CurrentFilter.SyncRuleId, Is.EqualTo(HrImportRule));
                Assert.That(cut.Instance.CurrentFilter.ConnectedSystemId, Is.EqualTo(CorporateAd));
            }
        });
    }

    [Test]
    public void Render_NothingHeld_SaysSoAsTheHealthyState()
    {
        _navigation.NavigateTo("/admin/operations?t=generated-values");

        var cut = Render<OperationsGeneratedValuesTab>();

        cut.WaitForAssertion(() => Assert.That(cut.FindAll("[data-testid='generated-values-empty']"), Has.Count.EqualTo(1)));
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
