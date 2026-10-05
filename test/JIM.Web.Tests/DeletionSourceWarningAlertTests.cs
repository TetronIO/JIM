// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Deletion Rules panel's warning about projecting Connected Systems that are not authoritative sources (#1256).
/// What is worth testing is when it shows and what it names: it must stay silent when there is nothing to say (a
/// warning on every standard configuration is one administrators learn to ignore), name every system it is about, and
/// add the immediate-deletion line only when no grace period stands between a stale source list and a deletion.
/// </summary>
[TestFixture]
public class DeletionSourceWarningAlertTests : JimComponentTestContext
{
    private IRenderedComponent<DeletionSourceWarningAlert> RenderAlert(string[] systemNames, bool noGracePeriod = false) =>
        Render<DeletionSourceWarningAlert>(p => p
            .Add(c => c.SystemNames, systemNames)
            .Add(c => c.ObjectTypeName, "Person")
            .Add(c => c.NoGracePeriod, noGracePeriod));

    [Test]
    public void DeletionSourceWarningAlert_NoSystems_RendersNothing()
    {
        var cut = RenderAlert([]);

        Assert.That(cut.HasComponent<MudAlert>(), Is.False);
    }

    [Test]
    public void DeletionSourceWarningAlert_OneSystem_RendersWarningNamingIt()
    {
        var cut = RenderAlert(["Partner Portal"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponent<MudAlert>().Instance.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(cut.Markup, Does.Contain("Partner Portal"));
        }
    }

    [Test]
    public void DeletionSourceWarningAlert_SeveralSystems_NamesEach()
    {
        var cut = RenderAlert(["Badge System", "Partner Portal"]);

        Assert.That(cut.Markup, Does.Contain("Badge System").And.Contain("Partner Portal"));
    }

    [Test]
    public void DeletionSourceWarningAlert_NoGracePeriod_AddsImmediateDeletionLine()
    {
        var withGrace = RenderAlert(["Partner Portal"], noGracePeriod: false);
        var withoutGrace = RenderAlert(["Partner Portal"], noGracePeriod: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withGrace.FindAll("[data-testid='jim-deletion-source-no-grace']"), Is.Empty);
            Assert.That(withoutGrace.FindAll("[data-testid='jim-deletion-source-no-grace']"), Has.Count.EqualTo(1));
        }
    }
}
