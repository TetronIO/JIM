// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Run Profile Execution Item's Attribute Values Not Imported section (#2066): every recorded value, and the parts
/// shown only when they apply.
/// </summary>
[TestFixture]
public class AttributeValuesNotImportedSectionTests : JimComponentTestContext
{
    private static ImportedAttributeValueErrors Recorded(int total, params (string Attribute, string? Value)[] values) => new()
    {
        Total = total,
        Values = values.Select(v => new ImportedAttributeValueError
        {
            AttributeName = v.Attribute,
            AttributeType = AttributeDataType.DateTime,
            Value = v.Value,
            Reason = "Not a valid date and time."
        }).ToList()
    };

    [Test]
    public void Render_RecordedValues_ShowsEachValueAsReceived()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p
            .Add(c => c.Errors, Recorded(2, ("startDate", "31/02/2026"), ("contractedHours", "37.5h"))));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("31/02/2026"));
            Assert.That(cut.Markup, Does.Contain("37.5h"));
        });
    }

    [Test]
    public void Render_MoreValuesThanTheItemKept_SaysSo()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p.Add(c => c.Errors, Recorded(3, ("startDate", "31/02/2026"))));

        Assert.That(cut.FindAll("[data-testid=jim-attribute-values-kept-note]"), Has.Count.EqualTo(1));
    }

    [Test]
    public void Render_EveryValueKept_SaysNothingAboutIt()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p.Add(c => c.Errors, Recorded(1, ("startDate", "31/02/2026"))));

        Assert.That(cut.FindAll("[data-testid=jim-attribute-values-kept-note]"), Is.Empty);
    }

    [Test]
    public void Render_SourceLocationGiven_NamesItInTheNextStep()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p
            .Add(c => c.Errors, Recorded(1, ("startDate", "31/02/2026")))
            .Add(c => c.SourceLocation, "hr-users.csv, row 4,182"));

        Assert.That(cut.Find("[data-testid=jim-attribute-values-source]").TextContent, Is.EqualTo("hr-users.csv, row 4,182"));
    }

    [Test]
    public void Render_NoSourceLocation_LeavesItOut()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p.Add(c => c.Errors, Recorded(1, ("startDate", "31/02/2026"))));

        Assert.That(cut.FindAll("[data-testid=jim-attribute-values-source]"), Is.Empty);
    }

    [Test]
    public void Render_ConnectedSystemKnown_LinksToItsSchemaOnTheObjectType()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p
            .Add(c => c.Errors, Recorded(1, ("startDate", "31/02/2026")))
            .Add(c => c.ConnectedSystemId, 4)
            .Add(c => c.ObjectTypeName, "person"));

        Assert.That(cut.Find("[data-testid=jim-attribute-values-schema-link]").GetAttribute("href"),
            Is.EqualTo("/admin/connected-systems/4?t=schema&ot=person"));
    }

    [Test]
    public void Render_ConnectedSystemUnknown_OffersNoSchemaLink()
    {
        var cut = Render<AttributeValuesNotImportedSection>(p => p.Add(c => c.Errors, Recorded(1, ("startDate", "31/02/2026"))));

        Assert.That(cut.FindAll("[data-testid=jim-attribute-values-schema-link]"), Is.Empty);
    }
}
