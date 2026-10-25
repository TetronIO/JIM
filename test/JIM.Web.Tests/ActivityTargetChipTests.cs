// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Core.DTOs;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What an Activity acted on, as the chip for that kind of object; anything with no chip of its own (a setting, a
/// certificate) is left to the page's own fallback.
/// </summary>
[TestFixture]
public class ActivityTargetChipTests : JimComponentTestContext
{
    private const string Fallback = "fallback-target";

    private IRenderedComponent<ActivityTargetChip> RenderTarget(Activity activity, string? connectedSystemName = null, MetaverseObjectHeader? metaverseObject = null) =>
        Render<ActivityTargetChip>(p => p
            .Add(c => c.Activity, activity)
            .Add(c => c.ConnectedSystemName, connectedSystemName)
            .Add(c => c.MetaverseObject, metaverseObject)
            .Add(c => c.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, $"<span data-testid=\"{Fallback}\">x</span>"))));

    private static Activity NewActivity(ActivityTargetType type, string? name = "Panoply AD") =>
        new() { Id = Guid.NewGuid(), TargetType = type, TargetName = name };

    [Test]
    public void Render_ConnectedSystem_IsTheSystemChipLinkingToIt()
    {
        var activity = NewActivity(ActivityTargetType.ConnectedSystem);
        activity.ConnectedSystemId = 2;

        var chip = RenderTarget(activity).FindComponent<ObjectChip>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.ConnectedSystem));
            Assert.That(chip.Name, Is.EqualTo("Panoply AD"));
            Assert.That(chip.Href, Is.EqualTo("/admin/connected-systems/2/"));
        }
    }

    [Test]
    public void Render_PasswordSynchronisationToASystem_IsTheSystemChip()
    {
        var activity = NewActivity(ActivityTargetType.PasswordSynchronisation);
        activity.ConnectedSystemId = 2;

        var chip = RenderTarget(activity).FindComponent<ObjectChip>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.ConnectedSystem));
            Assert.That(chip.Href, Is.EqualTo("/admin/connected-systems/2/"));
        }
    }

    [Test]
    public void Render_ConnectedSystemObject_IsTheObjectChipLinkingIntoTheConnectorSpace()
    {
        var csoId = Guid.NewGuid();
        var activity = NewActivity(ActivityTargetType.ConnectedSystemObject, "jsmith");
        activity.ConnectedSystemId = 2;
        activity.ConnectedSystemObjectId = csoId;

        var chip = RenderTarget(activity).FindComponent<ObjectChip>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.ConnectedSystemObject));
            Assert.That(chip.Href, Does.Contain(csoId.ToString()));
        }
    }

    [Test]
    public void Render_RunProfile_NamesItsConnectedSystem()
    {
        var activity = NewActivity(ActivityTargetType.ConnectedSystemRunProfile, "Full Import");
        activity.ConnectedSystemId = 2;

        var chip = RenderTarget(activity, connectedSystemName: "Panoply AD").FindComponent<ObjectChip>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.RunProfile));
            Assert.That(chip.TypeName, Is.EqualTo("Panoply AD"));
            Assert.That(chip.Name, Is.EqualTo("Full Import"));
        }
    }

    [Test]
    public void Render_MetaverseObject_IsTheObjectChipUnderItsType()
    {
        var header = new MetaverseObjectHeader { Id = Guid.NewGuid(), TypeName = "User", TypePluralName = "Users", CachedDisplayName = "Adam Newman" };
        var activity = NewActivity(ActivityTargetType.MetaverseObject, "Adam Newman");

        var chip = RenderTarget(activity, metaverseObject: header).FindComponent<ObjectChip>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Kind, Is.EqualTo(ObjectChipKind.MetaverseObject));
            Assert.That(chip.TypeName, Is.EqualTo("User"));
            Assert.That(chip.Href, Is.EqualTo($"/t/users/v/{header.Id}"));
        }
    }

    [TestCase(ActivityTargetType.SynchronisationRule, ObjectChipKind.SynchronisationRule)]
    [TestCase(ActivityTargetType.Schedule, ObjectChipKind.Schedule)]
    [TestCase(ActivityTargetType.ApiKey, ObjectChipKind.ApiKey)]
    public void Render_ObjectWithAChipKind_WearsIt(ActivityTargetType type, ObjectChipKind expected)
    {
        var activity = NewActivity(type, "Something");
        activity.SyncRuleId = 7;
        activity.ApiKeyId = Guid.NewGuid();

        Assert.That(RenderTarget(activity).FindComponent<ObjectChip>().Instance.Kind, Is.EqualTo(expected));
    }

    [TestCase(ActivityTargetType.ServiceSetting)]
    [TestCase(ActivityTargetType.TrustedCertificate)]
    [TestCase(ActivityTargetType.MetaverseObjectHousekeeping)]
    public void Render_TargetWithNoChipKind_RendersTheFallback(ActivityTargetType type)
    {
        var cut = RenderTarget(NewActivity(type, "Something"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll($"[data-testid={Fallback}]"), Has.Count.EqualTo(1));
            Assert.That(cut.HasComponent<ObjectChip>(), Is.False);
        }
    }

    [Test]
    public void Render_ConnectedSystemObjectNoLongerKnown_RendersTheFallback()
    {
        // The account was deleted since; there is nothing to link a chip to.
        var activity = NewActivity(ActivityTargetType.ConnectedSystemObject, "jsmith");

        Assert.That(RenderTarget(activity).FindAll($"[data-testid={Fallback}]"), Has.Count.EqualTo(1));
    }
}
