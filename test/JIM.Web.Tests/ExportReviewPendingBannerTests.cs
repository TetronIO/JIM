// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using Bunit.TestDoubles;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The banner on a Metaverse Object flagged for review against every export rule (#2615) is for administrators: it concerns
/// what JIM will do to the accounts the person has. The gate is the component's own, so a page that places it cannot forget
/// it.
/// </summary>
[TestFixture]
public class ExportReviewPendingBannerTests : JimComponentTestContext
{
    private const string Banner = "[data-testid='jim-export-review-banner']";
    private const string ChangedAttributes = "[data-testid='jim-export-review-changed-attributes']";

    private BunitAuthorizationContext _authorisation = null!;

    protected override void ConfigureAdditionalServices()
    {
        _authorisation = AddAuthorization();
        _authorisation.SetAuthorized("administrator");
    }

    [SetUp]
    public void SetUp() => _authorisation.SetRoles("User", "Administrator");

    [Test]
    public void Render_AdministratorAndObjectFlagged_ShowsTheBanner()
    {
        var cut = Render<ExportReviewPendingBanner>(p => p.Add(c => c.Pending, true));

        Assert.That(cut.FindAll(Banner), Has.Count.EqualTo(1));
    }

    [Test]
    public void Render_AdministratorAndObjectNotFlagged_ShowsNothing()
    {
        var cut = Render<ExportReviewPendingBanner>(p => p.Add(c => c.Pending, false));

        Assert.That(cut.FindAll(Banner), Is.Empty);
    }

    [Test]
    public void Render_ReaderWhoIsNotAnAdministrator_ShowsNothingEvenWhenTheObjectIsFlagged()
    {
        _authorisation.SetRoles("User");

        var cut = Render<ExportReviewPendingBanner>(p => p
            .Add(c => c.Pending, true)
            .Add(c => c.ChangedAttributeNames, ["Mobile"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(Banner), Is.Empty);
            Assert.That(cut.Markup, Does.Not.Contain("Mobile"), "what the object records must not reach a reader who cannot see the banner");
        }
    }

    [Test]
    public void Render_ChangesRecordedOutsideSynchronisation_NamesTheAttributes()
    {
        var cut = Render<ExportReviewPendingBanner>(p => p
            .Add(c => c.Pending, true)
            .Add(c => c.ChangedAttributeNames, ["Mobile", "Job Title"]));

        var line = cut.Find(ChangedAttributes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(line.TextContent, Does.Contain("Mobile"));
            Assert.That(line.TextContent, Does.Contain("Job Title"));
        }
    }

    [Test]
    public void Render_NoChangesRecordedOutsideSynchronisation_NamesNoAttributes()
    {
        var cut = Render<ExportReviewPendingBanner>(p => p.Add(c => c.Pending, true));

        Assert.That(cut.FindAll(ChangedAttributes), Is.Empty);
    }
}
