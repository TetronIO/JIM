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
/// The Collision Remediation switch on the generated Attribute Flow form (Unique Value Generation, #242, release 4, Phase
/// 9): available only when a system the value is exported to can report a collision, showing the flow's setting then, and
/// editing the generation in place (it saves with the rest of the generation settings) and telling the host.
/// </summary>
[TestFixture]
public class CollisionRemediationSwitchTests : JimComponentTestContext
{
    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static GeneratedValueParticipant Participant(int systemId, bool reportsCollisions) => new()
    {
        ConnectedSystemId = systemId,
        ConnectedSystemName = $"System {systemId}",
        ConnectorName = "JIM LDAP Connector",
        AttributeName = "sAMAccountName",
        ReportsCollisions = reportsCollisions
    };

    private IRenderedComponent<CollisionRemediationSwitch> RenderSwitch(SyncRuleMappingGeneration generation, IReadOnlyList<GeneratedValueParticipant>? participants, Action? onChanged = null) =>
        Render<CollisionRemediationSwitch>(p => p
            .Add(c => c.Generation, generation)
            .Add(c => c.Participants, participants)
            .Add(c => c.OnChanged, () => onChanged?.Invoke()));

    private static bool IsDisabled(IRenderedComponent<CollisionRemediationSwitch> cut) =>
        cut.Find("[data-testid='collision-remediation'] input").HasAttribute("disabled");

    private static bool IsOn(IRenderedComponent<CollisionRemediationSwitch> cut) =>
        cut.FindComponent<MudSwitch<bool>>().Instance.GetState(x => x.Value);

    [Test]
    public void Render_ASystemReportsCollisions_IsAvailableAndShowsTheSetting()
    {
        var cut = RenderSwitch(new SyncRuleMappingGeneration { CollisionRemediation = true }, [Participant(2, true), Participant(3, false)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisabled(cut), Is.False);
            Assert.That(IsOn(cut), Is.True);
            Assert.That(cut.FindAll("[data-testid='collision-remediation-unavailable']"), Is.Empty);
        }
    }

    [Test]
    public void Render_NoSystemReportsCollisions_IsDisabledOffAndSaysWhy()
    {
        var cut = RenderSwitch(new SyncRuleMappingGeneration { CollisionRemediation = true }, [Participant(3, false)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisabled(cut), Is.True);
            Assert.That(IsOn(cut), Is.False, "nothing can act on a collision, so the switch does not claim to");
            Assert.That(cut.FindAll("[data-testid='collision-remediation-unavailable']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_ParticipantsStillLoading_IsDisabledWithoutClaimingItIsUnavailable()
    {
        var cut = RenderSwitch(new SyncRuleMappingGeneration(), null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsDisabled(cut), Is.True);
            Assert.That(cut.FindAll("[data-testid='collision-remediation-unavailable']"), Is.Empty);
        }
    }

    [Test]
    public async Task SwitchedOff_EditsTheGenerationAndTellsTheHostAsync()
    {
        var generation = new SyncRuleMappingGeneration { CollisionRemediation = true };
        var changes = 0;
        var cut = RenderSwitch(generation, [Participant(2, true)], () => changes++);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generation.CollisionRemediation, Is.False);
            Assert.That(changes, Is.EqualTo(1));
        }
    }
}
