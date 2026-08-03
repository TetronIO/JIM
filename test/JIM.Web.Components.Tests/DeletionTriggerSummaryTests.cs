// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Covers the live plain-language deletion trigger summary (#119): the safety rail on the Metaverse Object
/// Type detail page that restates the full consequence (trigger mode, sources by name, grace period) in one
/// sentence so a misconfiguration is visible before Save.
/// </summary>
[TestFixture]
public class DeletionTriggerSummaryTests : JimComponentTestContext
{
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    private IRenderedComponent<DeletionTriggerSummary> RenderSummary(
        AuthoritativeSourceTriggerMode mode,
        IReadOnlyList<string> sourceNames,
        TimeSpan? gracePeriod)
    {
        return Render<DeletionTriggerSummary>(p => p
            .Add(c => c.ObjectTypeName, "Person")
            .Add(c => c.TriggerMode, mode)
            .Add(c => c.SourceNames, sourceNames)
            .Add(c => c.GracePeriod, gracePeriod));
    }

    [Test]
    public void DeletionTriggerSummary_NoSourcesSelected_ExplainsTheConfigurationCannotBeSaved()
    {
        var cut = RenderSummary(AuthoritativeSourceTriggerMode.AllSourcesDisconnect, [], ThirtyDays);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Select at least one authoritative source."));
            Assert.That(cut.Markup, Does.Contain("cannot be saved"));
        }
    }

    [Test]
    public void DeletionTriggerSummary_AllModeMultipleSources_StatesEverySourceMustDisconnect()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            ["HR (Workday)", "Active Directory"],
            ThirtyDays);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("HR (Workday)"));
            Assert.That(cut.Markup, Does.Contain("Active Directory"));
            Assert.That(cut.Markup, Does.Contain("disconnected"));
            Assert.That(cut.Markup, Does.Contain("While any of them remains connected the object is retained."));
        }
    }

    [Test]
    public void DeletionTriggerSummary_AllModeSingleSource_NamesTheSingleSource()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            ["HR (Workday)"],
            ThirtyDays);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("HR (Workday)"));
            Assert.That(cut.Markup, Does.Contain("disconnects."));
            Assert.That(cut.Markup, Does.Not.Contain("remains connected"));
        }
    }

    [Test]
    public void DeletionTriggerSummary_SpecificModeMultipleSources_StatesAnyOneSourceTriggersDeletion()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            ["HR (Workday)", "Active Directory"],
            ThirtyDays);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("any"));
            Assert.That(cut.Markup, Does.Contain("even if the others remain connected"));
            Assert.That(cut.Markup, Does.Contain("HR (Workday)"));
            Assert.That(cut.Markup, Does.Contain("Active Directory"));
        }
    }

    [Test]
    public void DeletionTriggerSummary_SpecificModeSingleSource_StatesOtherConnectorsAreDisregarded()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            ["HR (Workday)"],
            ThirtyDays);

        Assert.That(cut.Markup, Does.Contain("regardless of other connectors"));
    }

    [Test]
    public void DeletionTriggerSummary_WithGracePeriod_RendersTheGracePeriodAsACompoundAdjective()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            ["HR (Workday)"],
            ThirtyDays);

        using (Assert.EnterMultipleScope())
        {
            // "a 30 day grace period", not "a 30 days grace period"
            Assert.That(cut.Markup, Does.Contain("30 day"));
            Assert.That(cut.Markup, Does.Not.Contain("30 days"));
            Assert.That(cut.Markup, Does.Contain("grace period"));
        }
    }

    [Test]
    public void DeletionTriggerSummary_WithNoGracePeriod_StatesDeletionIsImmediate()
    {
        var cut = RenderSummary(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            ["HR (Workday)"],
            null);

        Assert.That(cut.Markup, Does.Contain("deleted immediately"));
    }
}
