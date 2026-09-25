// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the "Start again…" typed confirmation (Unique Value Generation, #242, Phase 3, Work Package D2,
/// mockup screen 02b): the confirm action stays disabled until the attribute name is typed exactly, matching
/// the shared <c>ConsequenceConfirmationDialog</c> rule it is built on. The restart itself (a full save path
/// through <c>ConnectedSystemServer</c>, including its Activity write) is covered at runtime rather than here.
/// </summary>
[TestFixture]
public class GeneratedValueStartAgainDialogTests : JimComponentTestContext
{
    private const string ConfirmButtonMarker = "jim-consequence-confirm";
    private const string PhraseFieldMarker = "jim-consequence-phrase";
    private const string AttributeName = "Employee Number";
    private const int MappingId = 7;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<ISyncRepository> _mockSyncRepository = null!;
    private JimApplication _jim = null!;

    protected override void ConfigureAdditionalServices()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockSyncRepository = new Mock<ISyncRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);

        _jim = new JimApplication(_mockRepository.Object, syncRepository: _mockSyncRepository.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [SetUp]
    public void SetUp()
    {
        _mockConnectedSystemRepository.Reset();
        _mockSyncRepository.Reset();

        var mapping = new SyncRuleMapping
        {
            Id = MappingId,
            TargetMetaverseAttributeId = 3,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 3, Name = AttributeName },
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 100456 }
        };
        _mockConnectedSystemRepository.Setup(r => r.GetSyncRuleMappingAsync(MappingId)).ReturnsAsync(mapping);
        _mockSyncRepository
            .Setup(r => r.GetGeneratedValueSequenceAsync(3, null))
            .ReturnsAsync(new GeneratedValueSequence { MetaverseAttributeId = 3, NextValue = 101701, AssignedCount = 1245 });
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private IRenderedComponent<MudDialogProvider> ShowDialog(long configuredStart = 100456L, int? fixedWidth = null)
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<GeneratedValueStartAgainDialog>
        {
            { x => x.MappingId, MappingId },
            { x => x.AttributeName, AttributeName },
            { x => x.ConfiguredStart, configuredStart },
            { x => x.FixedWidth, fixedWidth }
        };
        provider.InvokeAsync(() => dialogService.ShowAsync<GeneratedValueStartAgainDialog>("Start again?", parameters));
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");
        provider.WaitForState(() => !provider.Find($"[data-testid='{ConfirmButtonMarker}']").HasAttribute("disabled") ||
                                     provider.FindAll($"[data-testid='{PhraseFieldMarker}']").Count > 0 ||
                                     provider.Markup.Contains("changes nothing"));
        return provider;
    }

    [Test]
    public void GeneratedValueStartAgainDialog_Opens_ShowsCounterFromAndToWithNoThousandsSeparator()
    {
        // QA fix (#242 Phase 3 D2): these are identifiers, not quantities.
        var provider = ShowDialog();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("100456"));
            Assert.That(provider.Markup, Does.Contain("101701"));
            Assert.That(provider.Markup, Does.Not.Contain("100,456"));
            Assert.That(provider.Markup, Does.Not.Contain("101,701"));
        }
    }

    [Test]
    public void GeneratedValueStartAgainDialog_Opens_UsesCorrectedGrammarWithNoArticle()
    {
        // QA fix (#242 Phase 3 D2): "a Employee Number" is wrong for any vowel-starting attribute name; the
        // reworded sentence avoids the article entirely.
        var provider = ShowDialog();

        var normalised = string.Join(' ', provider.Markup.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(normalised, Does.Contain("Metaverse Objects that already have a value for Employee Number keep it"));
            Assert.That(normalised, Does.Contain("only Metaverse Objects without one are given a new value"));
        }
    }

    [Test]
    public void GeneratedValueStartAgainDialog_CounterAlreadyAtStartAt_ShowsNoOpMessageAndDisablesConfirm()
    {
        // The current counter (101701, per the mock's GetGeneratedValueSequenceAsync setup) is not the one under
        // test here: use a ConfiguredStart equal to it so the dialog reads "already at Start at".
        var provider = ShowDialog(configuredStart: 101701L);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("The counter is already at Start at (101701); starting again changes nothing."));
            var confirmButton = provider.Find($"[data-testid='{ConfirmButtonMarker}']");
            Assert.That(confirmButton.HasAttribute("disabled"), Is.True);
        }
    }

    [Test]
    public void GeneratedValueStartAgainDialog_CounterAlreadyAtStartAt_TypingPhraseStillDoesNotEnableConfirm()
    {
        var provider = ShowDialog(configuredStart: 101701L);

        provider.Find($"[data-testid='{PhraseFieldMarker}'] input").Input(AttributeName);

        var confirmButton = provider.Find($"[data-testid='{ConfirmButtonMarker}']");
        Assert.That(confirmButton.HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void GeneratedValueStartAgainDialog_NoPhraseTyped_DisablesConfirm()
    {
        var provider = ShowDialog();

        var confirmButton = provider.Find($"[data-testid='{ConfirmButtonMarker}']");
        Assert.That(confirmButton.HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void GeneratedValueStartAgainDialog_WrongPhraseTyped_DisablesConfirm()
    {
        var provider = ShowDialog();

        provider.Find($"[data-testid='{PhraseFieldMarker}'] input").Input("Employee Numbe");

        var confirmButton = provider.Find($"[data-testid='{ConfirmButtonMarker}']");
        Assert.That(confirmButton.HasAttribute("disabled"), Is.True);
    }

    [Test]
    public void GeneratedValueStartAgainDialog_ExactAttributeNameTyped_EnablesConfirm()
    {
        var provider = ShowDialog();

        provider.Find($"[data-testid='{PhraseFieldMarker}'] input").Input(AttributeName);

        provider.WaitForAssertion(() =>
        {
            var confirmButton = provider.Find($"[data-testid='{ConfirmButtonMarker}']");
            Assert.That(confirmButton.HasAttribute("disabled"), Is.False);
        });
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
