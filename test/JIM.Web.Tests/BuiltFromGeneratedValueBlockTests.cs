// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Pages.Admin.Components;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The "Built from generated value" block on the Attribute Flow dialog (Generated Value Forms, #2014, mock screen 1):
/// shown only for a flow built from a generated value, holding the flow's Keep unique switch, which stays changeable
/// while the generated flow's own Collision Remediation is off but is marked as not in effect.
/// </summary>
[TestFixture]
public class BuiltFromGeneratedValueBlockTests : JimComponentTestContext
{
    private const string BlockMarker = "[data-testid='built-from-generated-value']";
    private const string SwitchMarker = "[data-testid='built-from-generated-value-keep-unique']";
    private const string NotInEffectMarker = "[data-testid='built-from-generated-value-not-in-effect']";

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static GeneratedValueBuiltValueDescription Described(GeneratedValueBuiltValueKind kind = GeneratedValueBuiltValueKind.Form,
        bool collisionRemediation = true) => new()
    {
        GeneratedAttributeName = "Account Name",
        BuiltFromAttributeNames = kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues ? ["Account Name", "Badge Number"] : ["Account Name"],
        MetaverseAttributeName = "Email",
        Kind = kind,
        KeepUnique = true,
        CollisionRemediation = collisionRemediation
    };

    private IRenderedComponent<BuiltFromGeneratedValueBlock> RenderBlock(GeneratedValueBuiltValueDescription? described, SyncRuleMapping mapping, Action? onChanged = null) =>
        Render<BuiltFromGeneratedValueBlock>(ps => ps
            .Add(c => c.Description, described)
            .Add(c => c.Mapping, mapping)
            .Add(c => c.OnChanged, () => onChanged?.Invoke()));

    [Test]
    public void Render_FlowBuiltFromNoGeneratedValue_ShowsNothing()
    {
        var cut = RenderBlock(null, new SyncRuleMapping());

        Assert.That(cut.FindAll(BlockMarker), Is.Empty);
    }

    [Test]
    public void Render_FlowBuiltFromOneGeneratedValue_ShowsTheSwitchAsTheMappingHoldsIt()
    {
        var mapping = new SyncRuleMapping { KeepUnique = false };

        var cut = RenderBlock(Described(), mapping);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(BlockMarker), Has.Count.EqualTo(1));
            Assert.That(cut.FindComponent<MudSwitch<bool>>().Instance.GetState(x => x.Value), Is.False);
            Assert.That(cut.FindAll(NotInEffectMarker), Is.Empty);
        }
    }

    [Test]
    public async Task SwitchingItOff_SetsTheMappingsKeepUniqueAndTellsTheHostAsync()
    {
        var mapping = new SyncRuleMapping { KeepUnique = true };
        var changed = 0;
        var cut = RenderBlock(Described(), mapping, () => changed++);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.KeepUnique, Is.False);
            Assert.That(changed, Is.EqualTo(1));
        }
    }

    [Test]
    public void Render_GeneratedFlowsCollisionRemediationOff_MarksTheSwitchNotInEffectButLeavesItChangeable()
    {
        var cut = RenderBlock(Described(collisionRemediation: false), new SyncRuleMapping());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(NotInEffectMarker), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(SwitchMarker), Has.Count.EqualTo(1));
            Assert.That(cut.FindComponent<MudSwitch<bool>>().Instance.Disabled, Is.False,
                "the administrator can set it before turning Collision Remediation back on");
        }
    }

    [Test]
    public void Render_FlowBuiltFromSeveralGeneratedValues_ShowsTheBlockWithoutASwitch()
    {
        var cut = RenderBlock(Described(GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues), new SyncRuleMapping());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(BlockMarker), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(SwitchMarker), Is.Empty, "neither generated value can be corrected, so there is nothing to switch");
        }
    }
}
