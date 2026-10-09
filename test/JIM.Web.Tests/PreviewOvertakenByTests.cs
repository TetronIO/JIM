// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What overtook a preview, as the portal shows it (#2022): each thing it names is that thing's object chip, linking to
/// it, so an administrator reading "data has changed since this preview ran" can see at a glance which run on which
/// Connected System, and open it.
/// </summary>
[TestFixture]
public class PreviewOvertakenByTests : JimComponentTestContext
{
    [Test]
    public void PreviewOvertakenBy_ARun_ShowsTheRunProfileLinkingToTheRunAndTheSystemLinkingToItself()
    {
        var runId = Guid.NewGuid();
        var staleness = new ConfigurationChangePreviewStaleness(new PreviewOvertakingActivity(runId, DateTime.UtcNow,
            ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, "Full Import", "Glitterband EMEA", ConnectedSystemId: 2), null);

        var cut = Render<PreviewOvertakenBy>(p => p.Add(c => c.Staleness, staleness));
        var chips = cut.FindComponents<ObjectChip>().Select(c => c.Instance).ToList();

        Assert.That(chips.Select(c => (c.Kind, c.Name, c.Href)), Is.EqualTo(new[]
        {
            (ObjectChipKind.RunProfile, (string?)"Full Import", (string?)$"/activity/{runId}"),
            (ObjectChipKind.ConnectedSystem, "Glitterband EMEA", "/admin/connected-systems/2")
        }));
    }

    [Test]
    public void PreviewOvertakenBy_DataAndConfiguration_ShowsBoth()
    {
        var staleness = new ConfigurationChangePreviewStaleness(
            new PreviewOvertakingActivity(Guid.NewGuid(), DateTime.UtcNow, ActivityTargetType.ConnectedSystemRunProfile,
                ActivityTargetOperationType.Execute, "Full Import", "Glitterband EMEA", ConnectedSystemId: 2),
            new PreviewOvertakingActivity(Guid.NewGuid(), DateTime.UtcNow, ActivityTargetType.SynchronisationRule,
                ActivityTargetOperationType.Update, "HR Users", null, SyncRuleId: 9));

        var cut = Render<PreviewOvertakenBy>(p => p.Add(c => c.Staleness, staleness));

        Assert.That(cut.FindComponents<ObjectChip>().Select(c => c.Instance.Kind), Is.EqualTo(new[]
        {
            ObjectChipKind.RunProfile, ObjectChipKind.ConnectedSystem, ObjectChipKind.SynchronisationRule
        }));
    }

    [Test]
    public void PreviewOvertakenBy_Current_ShowsNothing()
    {
        var cut = Render<PreviewOvertakenBy>(p => p.Add(c => c.Staleness, new ConfigurationChangePreviewStaleness(null, null)));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
