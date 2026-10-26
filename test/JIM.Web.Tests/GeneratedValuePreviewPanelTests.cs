// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Generated Value form's result (#2471): the candidates are worked out from the base the tester tried, typed and
/// value-processed as a Synchronisation would, and only fall back to placeholder values while nothing has been tried.
/// </summary>
[TestFixture]
public class GeneratedValuePreviewPanelTests : JimComponentTestContext
{
    private const string BaseExpression = "Lower(cs[\"firstName\"]) + \".\" + Lower(cs[\"lastName\"])";

    [SetUp]
    public void SetUp()
    {
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(new Mock<IConnectedSystemRepository>().Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        // The existing-object count takes a while against a large Metaverse; long enough here for a settings change to
        // land while it is still running.
        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.CountMetaverseObjectsAwaitingGeneratedValueAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(async () => { await Task.Delay(600); return 42; });
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object, syncRepository.Object));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository, ISyncRepository syncRepository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository, syncRepository: syncRepository);
    }

    private IRenderedComponent<GeneratedValuePreviewPanel> RenderPreview(GeneratedBaseEvaluation? triedBase)
    {
        var mapping = new SyncRuleMapping { Generation = new SyncRuleMappingGeneration() };
        var source = new SyncRuleMappingSource { Expression = BaseExpression };
        return Render<GeneratedValuePreviewPanel>(p => p
            .Add(c => c.Mapping, mapping)
            .Add(c => c.BaseSource, source)
            .Add(c => c.TargetType, AttributeDataType.Text)
            .Add(c => c.TriedBase, triedBase));
    }

    [Test]
    public void GeneratedValuePreviewPanel_BaseTried_WorksTheCandidatesOutFromIt()
    {
        var cut = RenderPreview(new GeneratedBaseEvaluation { Outcome = GeneratedBaseOutcome.Base, BaseValue = "james.brown" });

        // Waited for as state rather than as an assertion: NUnit records every failed Assert.That, even one that bUnit
        // retries, so only the settled markup is asserted. The refresh is debounced.
        cut.WaitForState(() => cut.Markup.Contains("james.brown1"));

        Assert.That(cut.Markup, Does.Contain("james.brown1"), "the candidates follow the base the tester tried");
    }

    [Test]
    public void GeneratedValuePreviewPanel_NothingTried_ShowsAPlaceholderExample()
    {
        var cut = RenderPreview(null);

        cut.WaitForState(() => cut.Markup.Contains("firstname.lastname1"));

        Assert.That(cut.Markup, Does.Contain("firstname.lastname1"), "until values are tried, each input stands in as its own name");
    }

    [Test]
    public void GeneratedValuePreviewPanel_TriedBaseWaits_ShowsNoCandidates()
    {
        // A base that waits for an input gives generation nothing, so a candidate built from placeholders would claim a
        // value that would never be generated.
        var cut = RenderPreview(new GeneratedBaseEvaluation { Outcome = GeneratedBaseOutcome.Waits });

        cut.WaitForState(() => cut.FindAll("[data-testid=jim-generated-value-nothing-generated]").Count > 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid=jim-generated-value-nothing-generated]"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("firstname.lastname"));
        }
    }

    [Test]
    public async Task GeneratedValuePreviewPanel_SettingChangedWhileCounting_StillShowsTheCountAsync()
    {
        // A settings change cancelled the count in flight and the refresh it started did not count again, so the count's
        // spinner was left on screen for good.
        var mapping = new SyncRuleMapping { Generation = new SyncRuleMappingGeneration() };
        var cut = Render<GeneratedValuePreviewPanel>(p => p
            .Add(c => c.Mapping, mapping)
            .Add(c => c.BaseSource, new SyncRuleMappingSource { Expression = BaseExpression })
            .Add(c => c.TargetType, AttributeDataType.Text)
            .Add(c => c.TargetName, "Common Name")
            .Add(c => c.Direction, SyncRuleDirection.Import)
            .Add(c => c.SyncRuleId, 4)
            .Add(c => c.ConnectedSystemId, 2)
            .Add(c => c.MetaverseObjectTypeId, 1)
            .Add(c => c.TargetMetaverseAttributeId, 7));

        await Task.Delay(450);
        mapping.Generation.SuffixStart = 2;
        cut.Render();

        cut.WaitForState(() => cut.Markup.Contains("42 existing"), TimeSpan.FromSeconds(5));
        Assert.That(cut.Markup, Does.Contain("42 existing"));
    }
}
