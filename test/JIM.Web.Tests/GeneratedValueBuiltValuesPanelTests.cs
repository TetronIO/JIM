// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// "Values built from ..." on the generated Attribute Flow form (Generated Value Forms, #2014, mock screen 2): one row
/// per value built from the generated value, each saying what happens when a target refuses it, following the form's
/// own Collision Remediation switch as it stands.
/// </summary>
[TestFixture]
public class GeneratedValueBuiltValuesPanelTests : JimComponentTestContext
{
    private const string RowMarker = "[data-testid='generated-value-built-value-row']";
    private const string EmptyMarker = "[data-testid='generated-value-built-values-empty']";

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static GeneratedValueBuiltValueDescription Value(int mappingId, string name, GeneratedValueBuiltValueKind kind = GeneratedValueBuiltValueKind.Form,
        bool keepUnique = true, string syncRuleName = "HR Import", IReadOnlyList<string>? probedIn = null, string? moveTo = null) => new()
    {
        SyncRuleMappingId = mappingId,
        SyncRuleName = syncRuleName,
        CheckedBeforeIssuing = kind == GeneratedValueBuiltValueKind.Form && keepUnique,
        ProbedIn = probedIn ?? [],
        MoveToSyncRuleName = moveTo,
        GeneratedAttributeName = "Account Name",
        BuiltFromAttributeNames = kind == GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues ? ["Account Name", "Badge Number"] : ["Account Name"],
        MetaverseAttributeName = name,
        Kind = kind,
        KeepUnique = keepUnique,
        ExportedTo = [new GeneratedValueBuiltValueTarget { ConnectedSystemId = 2, ConnectedSystemName = "Corp AD", AttributeId = 20, AttributeName = "mail" }]
    };

    private IRenderedComponent<GeneratedValueBuiltValuesPanel> RenderPanel(IReadOnlyList<GeneratedValueBuiltValueDescription>? values, bool collisionRemediation = true) =>
        Render<GeneratedValueBuiltValuesPanel>(ps => ps
            .Add(c => c.BuiltValues, values)
            .Add(c => c.GeneratedAttributeName, "Account Name")
            .Add(c => c.CollisionRemediation, collisionRemediation));

    private static string[] Outcomes(IRenderedComponent<GeneratedValueBuiltValuesPanel> cut) =>
        cut.FindAll(RowMarker).Select(row => row.GetAttribute("data-outcome") ?? string.Empty).ToArray();

    private static string[] BeforeIssuing(IRenderedComponent<GeneratedValueBuiltValuesPanel> cut) =>
        cut.FindAll(RowMarker).Select(row => row.GetAttribute("data-before-issuing") ?? string.Empty).ToArray();

    [Test]
    public void Render_WhileLoading_ShowsNoRowsAndNoEmptyText()
    {
        var cut = RenderPanel(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RowMarker), Is.Empty);
            Assert.That(cut.FindAll(EmptyMarker), Is.Empty);
        }
    }

    [Test]
    public void Render_NothingBuiltFromTheValue_SaysSo()
    {
        var cut = RenderPanel([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RowMarker), Is.Empty);
            Assert.That(cut.FindAll(EmptyMarker), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_EachValue_SaysWhatHappensWhenATargetRefusesIt()
    {
        var cut = RenderPanel([
            Value(1, "Email"),
            Value(2, "Mailbox Address", keepUnique: false),
            Value(3, "Badge Login", GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
        ]);

        Assert.That(Outcomes(cut), Is.EqualTo(new[]
        {
            nameof(GeneratedValueBuiltValueRefusal.GeneratedValueCorrected),
            nameof(GeneratedValueBuiltValueRefusal.KeepUniqueOff),
            nameof(GeneratedValueBuiltValueRefusal.BuiltFromSeveral)
        }));
    }

    [Test]
    public void Render_FormsCollisionRemediationOff_NoValueIsCorrected()
    {
        var cut = RenderPanel([Value(1, "Email"), Value(2, "Mailbox Address", keepUnique: false)], collisionRemediation: false);

        Assert.That(Outcomes(cut), Is.All.EqualTo(nameof(GeneratedValueBuiltValueRefusal.CollisionRemediationOff)));
    }

    [Test]
    public void Render_EachValue_SaysWhetherItIsCheckedBeforeIssuing()
    {
        // Generated Value Forms (#2023).
        var cut = RenderPanel([
            Value(1, "Email", probedIn: ["Corp AD"]),
            Value(2, "Mailbox Address", GeneratedValueBuiltValueKind.BuiltOnAnotherRule, syncRuleName: "Mail Import", moveTo: "HR Import"),
            Value(3, "Display Email", keepUnique: false),
            Value(4, "Badge Login", GeneratedValueBuiltValueKind.BuiltFromSeveralGeneratedValues)
        ]);

        Assert.That(BeforeIssuing(cut), Is.EqualTo(new[]
        {
            nameof(GeneratedValueBuiltValueBeforeIssuing.Checked),
            nameof(GeneratedValueBuiltValueBeforeIssuing.BuiltOnAnotherRule),
            nameof(GeneratedValueBuiltValueBeforeIssuing.KeepUniqueOff),
            nameof(GeneratedValueBuiltValueBeforeIssuing.BuiltFromSeveral)
        }));
    }
}
